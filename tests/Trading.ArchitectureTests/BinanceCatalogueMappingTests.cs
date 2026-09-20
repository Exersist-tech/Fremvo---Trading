using Trading.Application.Universe;
using Trading.Domain.Universe;
using Trading.Exchanges.Abstractions.Catalogue;
using Trading.Exchanges.Binance.Catalogue;

namespace Trading.ArchitectureTests;

public sealed class BinanceCatalogueMappingTests
{
    private const string Document = """
    {
      "symbols": [
        {
          "symbol": "BTCUSDT",
          "status": "TRADING",
          "baseAsset": "BTC",
          "quoteAsset": "USDT",
          "permissions": ["SPOT", "MARGIN"],
          "onboardDate": 1500000000000,
          "filters": [
            { "filterType": "PRICE_FILTER", "tickSize": "0.01000000" },
            { "filterType": "LOT_SIZE", "stepSize": "0.00001000", "minQty": "0.00001000" },
            { "filterType": "NOTIONAL", "minNotional": "5.00000000" }
          ]
        },
        {
          "symbol": "HALTUSDT",
          "status": "BREAK",
          "baseAsset": "HALT",
          "quoteAsset": "USDT",
          "permissionSets": [["SPOT"]],
          "filters": [
            { "filterType": "PRICE_FILTER", "tickSize": "0.01000000" }
          ]
        }
      ]
    }
    """;

    private static readonly string[] SeedAssets = { "BTC", "ETH" };

    private static readonly string[] SpotPermission = { "SPOT" };

    [Fact]
    public void MapsBinanceSymbolsToNeutralEntries()
    {
        var entries = BinanceExchangeInfoMapper.MapDocument(Document);

        Assert.Equal(2, entries.Count);
        var btc = entries[0];
        Assert.Equal("BTCUSDT", btc.ExchangeSymbol);
        Assert.Equal("BTC", btc.BaseAsset);
        Assert.Equal("USDT", btc.QuoteAsset);
        Assert.Equal("TRADING", btc.Status);
        Assert.Contains("SPOT", btc.Permissions, StringComparer.Ordinal);
    }

    [Fact]
    public void ParsesTradingRulesAsDecimalWithFullPrecision()
    {
        var btc = BinanceExchangeInfoMapper.MapDocument(Document)[0];

        // A binary floating-point type could not hold these exactly.
        Assert.Equal(0.01000000m, btc.PriceTickSize);
        Assert.Equal(0.00001000m, btc.QuantityStepSize);
        Assert.Equal(0.00001000m, btc.MinimumQuantity);
        Assert.Equal(5.00000000m, btc.MinimumNotional);
        Assert.True(btc.HasCompleteFilters);
    }

    [Fact]
    public void ParsesOnboardDateAsUtc()
    {
        var btc = BinanceExchangeInfoMapper.MapDocument(Document)[0];

        Assert.NotNull(btc.OnboardUtc);
        Assert.Equal(TimeSpan.Zero, btc.OnboardUtc!.Value.Offset);
    }

    [Fact]
    public void IncompleteFiltersAreNullRatherThanDefaulted()
    {
        var halt = BinanceExchangeInfoMapper.MapDocument(Document)[1];

        Assert.Null(halt.QuantityStepSize);
        Assert.Null(halt.MinimumNotional);
        Assert.False(halt.HasCompleteFilters);
    }

    [Fact]
    public void FlattensPermissionSets()
    {
        var halt = BinanceExchangeInfoMapper.MapDocument(Document)[1];

        Assert.Contains("SPOT", halt.Permissions, StringComparer.Ordinal);
    }

    [Fact]
    public void ExchangeStatusIsCarriedThroughWithoutInterpretation()
    {
        var halt = BinanceExchangeInfoMapper.MapDocument(Document)[1];

        Assert.Equal("BREAK", halt.Status);
    }

    [Fact]
    public void SymbolMissingIdentityIsSkippedRatherThanGuessed()
    {
        const string json = """
        { "symbols": [ { "symbol": "XUSDT", "status": "TRADING", "quoteAsset": "USDT" } ] }
        """;

        Assert.Empty(BinanceExchangeInfoMapper.MapDocument(json));
    }

    [Fact]
    public void MalformedDocumentThrowsRatherThanReportingAnEmptyCatalogue()
    {
        // An empty catalogue would look like a mass delisting and suspend
        // every instrument, so a parse failure must never be silent.
        Assert.Throws<FormatException>(() => BinanceExchangeInfoMapper.MapDocument("{ not json"));
        Assert.Throws<FormatException>(() => BinanceExchangeInfoMapper.MapDocument("{}"));
    }

    [Fact]
    public void ZeroEncodedRuleIsTreatedAsAbsent()
    {
        const string json = """
        {
          "symbols": [ {
            "symbol": "AUSDT", "status": "TRADING", "baseAsset": "A", "quoteAsset": "USDT",
            "permissions": ["SPOT"],
            "filters": [ { "filterType": "PRICE_FILTER", "tickSize": "0.00000000" } ]
          } ]
        }
        """;

        Assert.Null(BinanceExchangeInfoMapper.MapDocument(json)[0].PriceTickSize);
    }

    [Fact]
    public void NeutralEntryRejectsNonPositiveTradingRules()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstrumentCatalogueEntry(
            "BTCUSDT", "BTC", "USDT", "TRADING", Array.Empty<string>(), priceTickSize: -1m));
    }

    [Fact]
    public void SnapshotRejectsDuplicateSymbols()
    {
        var entry = new InstrumentCatalogueEntry("BTCUSDT", "BTC", "USDT", "TRADING", Array.Empty<string>());

        Assert.Throws<ArgumentException>(() => InstrumentCatalogueSnapshot.CreateComplete(
            "Binance", new[] { entry, entry }, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void PartialSnapshotMustStateWhyItIsIncomplete()
    {
        Assert.Throws<ArgumentException>(() => InstrumentCatalogueSnapshot.CreatePartial(
            "Binance", Array.Empty<InstrumentCatalogueEntry>(), DateTimeOffset.UnixEpoch, "  "));
    }

    [Fact]
    public void CompleteSnapshotAbsenceSuspendsTheInstrument()
    {
        var instrument = Seed("ETHUSDT", "ETH");
        instrument.ObserveInCatalogue("TRADING", SpotPermission, DateTimeOffset.UnixEpoch);
        instrument.MarkFiltersLoaded(DateTimeOffset.UnixEpoch);

        var snapshot = InstrumentCatalogueSnapshot.CreateComplete(
            "Binance", Array.Empty<InstrumentCatalogueEntry>(), DateTimeOffset.UnixEpoch.AddDays(1));

        var report = Synchronizer().Synchronize(
            new[] { instrument }, snapshot, new List<Instrument>(), _ => Guid.NewGuid());

        Assert.Equal(InstrumentState.Suspended, instrument.State);
        Assert.False(instrument.FiltersLoaded);
        Assert.Equal(1, report.CountOf(CatalogueSyncOutcome.SuspendedAsAbsent));
    }

    [Fact]
    public void PartialSnapshotAbsenceDoesNotSuspendTheInstrument()
    {
        // A transport fault must never suspend the whole universe.
        var instrument = Seed("ETHUSDT", "ETH");
        instrument.ObserveInCatalogue("TRADING", SpotPermission, DateTimeOffset.UnixEpoch);

        var snapshot = InstrumentCatalogueSnapshot.CreatePartial(
            "Binance", Array.Empty<InstrumentCatalogueEntry>(), DateTimeOffset.UnixEpoch.AddDays(1),
            "The exchange returned a truncated response.");

        var report = Synchronizer().Synchronize(
            new[] { instrument }, snapshot, new List<Instrument>(), _ => Guid.NewGuid());

        Assert.Equal(InstrumentState.Tracked, instrument.State);
        Assert.Equal(1, report.CountOf(CatalogueSyncOutcome.AbsenceNotActedOn));
    }

    [Fact]
    public void IncompleteFiltersClearTheLoadedFlag()
    {
        var instrument = Seed("HALTUSDT", "HALT");
        instrument.MarkFiltersLoaded(DateTimeOffset.UnixEpoch);

        var entries = BinanceExchangeInfoMapper.MapDocument(Document);
        var snapshot = InstrumentCatalogueSnapshot.CreateComplete(
            "Binance", entries, DateTimeOffset.UnixEpoch.AddDays(1));

        Synchronizer().Synchronize(
            new[] { instrument }, snapshot, new List<Instrument>(), _ => Guid.NewGuid());

        Assert.False(instrument.FiltersLoaded);
    }

    [Fact]
    public void DiscoveredInstrumentsAreTrackedAndConferNothing()
    {
        var snapshot = InstrumentCatalogueSnapshot.CreateComplete(
            "Binance", BinanceExchangeInfoMapper.MapDocument(Document), DateTimeOffset.UnixEpoch);

        var discovered = new List<Instrument>();
        Synchronizer().Synchronize(
            Array.Empty<Instrument>(), snapshot, discovered, _ => Guid.NewGuid());

        Assert.Equal(2, discovered.Count);
        Assert.All(discovered, instrument =>
            Assert.True(instrument.State is InstrumentState.Tracked or InstrumentState.Suspended));
        Assert.All(discovered, instrument => Assert.False(instrument.IsConfiguredSeed));
    }

    [Fact]
    public void RemovedInstrumentIsNeverReinstatedBySynchronisation()
    {
        var instrument = Seed("BTCUSDT", "BTC");
        instrument.Remove("Delisted.", DateTimeOffset.UnixEpoch);

        var snapshot = InstrumentCatalogueSnapshot.CreateComplete(
            "Binance", BinanceExchangeInfoMapper.MapDocument(Document), DateTimeOffset.UnixEpoch.AddDays(1));

        Synchronizer().Synchronize(
            new[] { instrument }, snapshot, new List<Instrument>(), _ => Guid.NewGuid());

        Assert.Equal(InstrumentState.Removed, instrument.State);
    }

    private static CatalogueSynchronizer Synchronizer() => new(new AssetClassifier(SeedAssets));

    private static Instrument Seed(string symbol, string baseAsset) =>
        Instrument.CreateSeed(Guid.NewGuid(), "Binance", symbol, baseAsset, "USDT", AssetClass.Cryptocurrency);
}
