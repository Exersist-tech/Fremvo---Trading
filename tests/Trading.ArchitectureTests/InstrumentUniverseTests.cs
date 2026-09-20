using Trading.Domain.Universe;
using Xunit;

namespace Trading.ArchitectureTests;

public sealed class InstrumentUniverseTests
{
    private static readonly string[] UsdtOnly = { "USDT" };

    private static readonly string[] SpotPermissionOnly = { "SPOT" };

    private static readonly string[] SpotAndMarginPermissions = { "SPOT", "MARGIN" };

    private static readonly string[] MarginPermissionOnly = { "MARGIN" };

    private static readonly string[] ConflictingAssetConfiguration = { "BTC", "USDT" };

    private static readonly string[] KnownCryptocurrencies =
    {
        "BTC", "ETH", "BNB", "XRP", "SOL", "DOGE", "ADA", "LINK", "PEPE", "TRUMP"
    };

    private static AssetClassifier Classifier() => new(KnownCryptocurrencies);

    private static Instrument Seed(string symbol, string baseAsset, string quoteAsset, AssetClass assetClass) =>
        Instrument.CreateSeed(Guid.NewGuid(), "Kraken", symbol, baseAsset, quoteAsset, assetClass);

    private static Instrument TradingSpotInstrument(string symbol = "BTCUSDT", string baseAsset = "BTC")
    {
        var instrument = Seed(symbol, baseAsset, "USDT", AssetClass.Cryptocurrency);
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotAndMarginPermissions, DateTimeOffset.UnixEpoch);
        return instrument;
    }

    [Fact]
    public void NewInstrumentIsTrackedOnlyAndGrantsNothing()
    {
        var instrument = Seed("BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);

        Assert.Equal(InstrumentState.Tracked, instrument.State);
        Assert.False(instrument.IsPresentOnExchange);
        Assert.False(instrument.HasSpotPermission);
        Assert.False(instrument.FiltersLoaded);
        Assert.True(instrument.BlocksNewExposure);
    }

    [Fact]
    public void ConfiguredSeedMembershipDoesNotImplyPresenceOnTheExchange()
    {
        var instrument = Seed("PROVEUSDT", "PROVE", "USDT", AssetClass.Unknown);

        Assert.True(instrument.IsConfiguredSeed);
        Assert.False(instrument.IsPresentOnExchange);
        Assert.Equal(InstrumentExclusionReason.NotClassified, instrument.EvaluateExclusion(UsdtOnly));
    }

    [Fact]
    public void InstrumentAbsentFromTheCatalogueIsSuspendedRatherThanDropped()
    {
        var instrument = TradingSpotInstrument();

        instrument.MarkAbsentFromCatalogue(DateTimeOffset.UnixEpoch, "Not listed on the exchange.");

        Assert.Equal(InstrumentState.Suspended, instrument.State);
        Assert.False(instrument.IsPresentOnExchange);
        Assert.Empty(instrument.Permissions);
        Assert.Equal(InstrumentExclusionReason.NotPresentOnExchange, instrument.EvaluateExclusion(UsdtOnly));
    }

    [Fact]
    public void StatusOtherThanTradingIsExcluded()
    {
        var instrument = Seed("BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Halted, "MAINTENANCE", SpotPermissionOnly, DateTimeOffset.UnixEpoch);

        Assert.False(instrument.IsTradingOnExchange);
        Assert.Equal(InstrumentExclusionReason.ExchangeStatusNotTrading, instrument.EvaluateExclusion(UsdtOnly));
        Assert.True(instrument.BlocksNewExposure);
    }

    [Fact]
    public void MissingSpotPermissionIsExcluded()
    {
        var instrument = Seed("BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", MarginPermissionOnly, DateTimeOffset.UnixEpoch);

        Assert.Equal(InstrumentExclusionReason.SpotPermissionMissing, instrument.EvaluateExclusion(UsdtOnly));
    }

    [Fact]
    public void QuoteAssetOutsideTheAllowlistIsExcluded()
    {
        var instrument = Seed("BTCFDUSD", "BTC", "FDUSD", AssetClass.Cryptocurrency);
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotPermissionOnly, DateTimeOffset.UnixEpoch);

        Assert.Equal(InstrumentExclusionReason.QuoteAssetNotAllowed, instrument.EvaluateExclusion(UsdtOnly));
    }

    [Theory]
    [InlineData(AssetClass.Stablecoin, InstrumentExclusionReason.StablecoinPair)]
    [InlineData(AssetClass.TokenizedEquity, InstrumentExclusionReason.TokenizedEquity)]
    [InlineData(AssetClass.Fiat, InstrumentExclusionReason.Fiat)]
    [InlineData(AssetClass.LeveragedToken, InstrumentExclusionReason.LeveragedToken)]
    [InlineData(AssetClass.Unknown, InstrumentExclusionReason.NotClassified)]
    public void ExcludedAssetClassesAreBarredEvenWhenEveryCatalogueGatePasses(
        AssetClass assetClass,
        InstrumentExclusionReason expected)
    {
        var instrument = Seed("XXXUSDT", "XXX", "USDT", assetClass);
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotPermissionOnly, DateTimeOffset.UnixEpoch);
        instrument.MarkFiltersLoaded(DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, instrument.EvaluateExclusion(UsdtOnly));
        Assert.True(instrument.IsExcluded(UsdtOnly));
    }

    [Fact]
    public void FullyQualifiedCryptocurrencyIsNotExcluded()
    {
        var instrument = TradingSpotInstrument();

        Assert.Equal(InstrumentExclusionReason.None, instrument.EvaluateExclusion(UsdtOnly));
        Assert.False(instrument.IsExcluded(UsdtOnly));
        Assert.False(instrument.BlocksNewExposure);
    }

    [Fact]
    public void SuspensionBlocksNewExposureAndPreservesTheReason()
    {
        var instrument = TradingSpotInstrument();

        instrument.Suspend("Stale market data.", DateTimeOffset.UnixEpoch);

        Assert.Equal(InstrumentState.Suspended, instrument.State);
        Assert.Equal("Stale market data.", instrument.SuspensionReason);
        Assert.True(instrument.BlocksNewExposure);
    }

    [Fact]
    public void LiftingASuspensionReturnsToTrackedOnlyAndRestoresNoEligibility()
    {
        var instrument = TradingSpotInstrument();
        instrument.Suspend("Stale market data.", DateTimeOffset.UnixEpoch);

        instrument.LiftSuspension();

        Assert.Equal(InstrumentState.Tracked, instrument.State);
        Assert.Null(instrument.SuspensionReason);
    }

    [Fact]
    public void LiftingASuspensionOnAnUnsuspendedInstrumentIsRejected()
    {
        var instrument = TradingSpotInstrument();

        Assert.Throws<InvalidOperationException>(instrument.LiftSuspension);
    }

    [Fact]
    public void RemovedInstrumentCannotBeReinstatedByACatalogueSynchronisation()
    {
        var instrument = TradingSpotInstrument();
        instrument.Remove("Delisted.", DateTimeOffset.UnixEpoch);

        Assert.Equal(InstrumentState.Removed, instrument.State);
        Assert.Throws<InvalidOperationException>(() =>
            instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotPermissionOnly, DateTimeOffset.UnixEpoch));
        Assert.Equal(InstrumentExclusionReason.Removed, instrument.EvaluateExclusion(UsdtOnly));
    }

    [Fact]
    public void ClearingFiltersRemovesTheLoadedTimestampRatherThanLeavingItStale()
    {
        var instrument = TradingSpotInstrument();
        instrument.MarkFiltersLoaded(DateTimeOffset.UnixEpoch);
        Assert.True(instrument.FiltersLoaded);

        instrument.ClearFilters();

        Assert.False(instrument.FiltersLoaded);
        Assert.Null(instrument.FiltersLoadedAtUtc);
    }

    [Fact]
    public void FirstObservedCandleKeepsTheEarliestTime()
    {
        var instrument = TradingSpotInstrument();
        var later = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var earlier = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        instrument.RecordFirstObservedCandle(later);
        instrument.RecordFirstObservedCandle(earlier);

        Assert.Equal(earlier, instrument.FirstObservedCandleUtc);
    }

    [Fact]
    public void ListingAgePrefersTheExchangeOnboardingTime()
    {
        var instrument = Seed("BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);
        var onboard = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotPermissionOnly, onboard, onboard);
        instrument.RecordFirstObservedCandle(new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero));

        var age = instrument.ListingAge(new DateTimeOffset(2024, 1, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(TimeSpan.FromDays(30), age);
    }

    [Fact]
    public void ListingAgeIsUnknownWithoutEvidence()
    {
        var instrument = Seed("BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);

        Assert.Null(instrument.ListingAge(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ReclassifyingAwayFromCryptocurrencySuspendsTheInstrument()
    {
        var instrument = TradingSpotInstrument();

        instrument.Reclassify(AssetClass.Stablecoin, DateTimeOffset.UnixEpoch);

        Assert.Equal(InstrumentState.Suspended, instrument.State);
        Assert.Equal(InstrumentExclusionReason.StablecoinPair, instrument.EvaluateExclusion(UsdtOnly));
    }

    [Fact]
    public void SymbolAndAssetCodesAreNormalisedSoLookupsCannotDiverge()
    {
        var instrument = Instrument.CreateFromCatalogue(
            Guid.NewGuid(), "Kraken", " btcusdt ", " btc ", " usdt ", AssetClass.Cryptocurrency);

        Assert.Equal("BTCUSDT", instrument.ExchangeSymbol);
        Assert.Equal("BTC", instrument.BaseAsset);
        Assert.Equal("USDT", instrument.QuoteAsset);
    }

    [Fact]
    public void EmptyIdentifiersAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Instrument.CreateSeed(Guid.Empty, "Kraken", "BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency));
        Assert.Throws<ArgumentException>(() =>
            Instrument.CreateSeed(Guid.NewGuid(), "Kraken", " ", "BTC", "USDT", AssetClass.Cryptocurrency));
    }

    // --- Asset classification -------------------------------------------------

    [Fact]
    public void UnrecognisedAssetIsUnknownRatherThanAssumedToBeACryptocurrency()
    {
        Assert.Equal(AssetClass.Unknown, Classifier().Classify("ZAMA"));
    }

    [Theory]
    [InlineData("USDT")]
    [InlineData("USDC")]
    [InlineData("FDUSD")]
    [InlineData("DAI")]
    public void KnownStablecoinsAreClassifiedAsStablecoins(string code)
    {
        Assert.Equal(AssetClass.Stablecoin, Classifier().Classify(code));
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("EUR")]
    [InlineData("NOK")]
    public void FiatCodesAreClassifiedAsFiat(string code)
    {
        Assert.Equal(AssetClass.Fiat, Classifier().Classify(code));
    }

    [Theory]
    [InlineData("BTCUP")]
    [InlineData("ETHDOWN")]
    [InlineData("BTC3L")]
    [InlineData("ADABULL")]
    public void LeveragedTokensAreClassifiedAsLeveragedTokens(string code)
    {
        Assert.Equal(AssetClass.LeveragedToken, Classifier().Classify(code));
    }

    [Fact]
    public void ACodeEndingInALeveragedSuffixIsNotALeveragedTokenWhenTheStemIsUnknown()
    {
        // "SOUP" ends with the "UP" suffix, but "SO" is not a recognised
        // asset, so it must not be misclassified as a leveraged BTCUP-style
        // token. It is simply unknown, which is still excluded.
        Assert.Equal(AssetClass.Unknown, Classifier().Classify("SOUP"));
    }

    [Fact]
    public void KnownCryptocurrencyIsClassifiedAsCryptocurrency()
    {
        Assert.Equal(AssetClass.Cryptocurrency, Classifier().Classify("BTC"));
    }

    [Fact]
    public void ClassificationIsCaseAndWhitespaceInsensitive()
    {
        Assert.Equal(AssetClass.Cryptocurrency, Classifier().Classify(" btc "));
        Assert.Equal(AssetClass.Stablecoin, Classifier().Classify(" usdt "));
    }

    [Fact]
    public void AnAssetListedAsBothCryptocurrencyAndStablecoinIsRejectedAtConfiguration()
    {
        Assert.Throws<ArgumentException>(() => new AssetClassifier(ConflictingAssetConfiguration));
    }

    [Fact]
    public void BlankAssetCodeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Classifier().Classify(" "));
    }

    [Fact]
    public void StablecoinToStablecoinPairIsExcludedFromTheResearchUniverse()
    {
        var classifier = Classifier();
        var baseClass = classifier.Classify("USDC");
        var instrument = Seed("USDCUSDT", "USDC", "USDT", baseClass);
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotPermissionOnly, DateTimeOffset.UnixEpoch);

        Assert.Equal(InstrumentExclusionReason.StablecoinPair, instrument.EvaluateExclusion(UsdtOnly));
    }
}
