using Trading.Domain.Universe;

namespace Trading.ArchitectureTests;

public sealed class MarketUniverseSeedTests
{
    [Fact]
    public void SeedContainsFiftyDistinctSymbols()
    {
        var symbols = MarketUniverseSeed.SeedSymbols;

        Assert.Equal(50, symbols.Count);
        Assert.Equal(50, symbols.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EverySeedSymbolIsQuotedInUsd()
    {
        foreach (var symbol in MarketUniverseSeed.SeedSymbols)
        {
            Assert.EndsWith("USD", symbol, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SeededInstrumentsAreTrackedOnly()
    {
        foreach (var instrument in MarketUniverseSeed.Create())
        {
            Assert.Equal(InstrumentState.Tracked, instrument.State);
        }
    }

    [Fact]
    public void SeededInstrumentsAreNotAssumedToExistOnTheExchange()
    {
        foreach (var instrument in MarketUniverseSeed.Create())
        {
            Assert.Null(instrument.LastCatalogueSyncUtc);
            Assert.False(instrument.IsPresentOnExchange);
        }
    }

    [Fact]
    public void SeededInstrumentsAreMarkedAsConfiguredSeed()
    {
        foreach (var instrument in MarketUniverseSeed.Create())
        {
            Assert.True(instrument.IsConfiguredSeed);
        }
    }

    [Fact]
    public void SeededInstrumentsBlockNewExposure()
    {
        // Membership of the seed must grant nothing. Until the catalogue is
        // observed and gates pass, no seeded instrument may be traded.
        foreach (var instrument in MarketUniverseSeed.Create())
        {
            Assert.True(instrument.BlocksNewExposure);
        }
    }

    [Fact]
    public void EverySeedBaseAssetClassifiesAsCryptocurrency()
    {
        var classifier = MarketUniverseSeed.CreateClassifier();

        foreach (var asset in MarketUniverseSeed.SeedBaseAssets)
        {
            Assert.Equal(AssetClass.Cryptocurrency, classifier.Classify(asset));
        }
    }

    [Fact]
    public void ShortSeedTickersAreNotMisreadAsLeveragedTokens()
    {
        // "OP" is a genuine short ticker, not a leveraged product, and must
        // not be excluded by the leveraged-token rule. "XBT" and "XDG" are
        // Kraken's own codes for Bitcoin and Dogecoin.
        var classifier = MarketUniverseSeed.CreateClassifier();

        Assert.Equal(AssetClass.Cryptocurrency, classifier.Classify("OP"));
        Assert.Equal(AssetClass.Cryptocurrency, classifier.Classify("XBT"));
        Assert.Equal(AssetClass.Cryptocurrency, classifier.Classify("XDG"));
    }

    [Fact]
    public void SeedExcludesStablecoinBaseAssets()
    {
        foreach (var asset in MarketUniverseSeed.SeedBaseAssets)
        {
            Assert.NotEqual("USDC", asset, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("FDUSD", asset, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("TUSD", asset, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual("DAI", asset, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SeedDoesNotIncludeItsOwnQuoteAssetAsABase()
    {
        Assert.DoesNotContain(
            MarketUniverseSeed.QuoteAsset,
            MarketUniverseSeed.SeedBaseAssets,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeedIsVersionedSoDecisionsCanBeTraced()
    {
        Assert.False(string.IsNullOrWhiteSpace(MarketUniverseSeed.Version));
    }

    [Fact]
    public void SeedCreationIsDeterministicWhenIdsAreSupplied()
    {
        static Guid Id(string symbol)
        {
            var bytes = new byte[16];
            for (var i = 0; i < symbol.Length; i++)
            {
                bytes[i % 16] ^= (byte)symbol[i];
            }

            return new Guid(bytes);
        }

        var first = MarketUniverseSeed.Create(Id);
        var second = MarketUniverseSeed.Create(Id);

        Assert.Equal(
            first.Select(i => i.Id).ToList(),
            second.Select(i => i.Id).ToList());
        Assert.Equal(
            first.Select(i => i.ExchangeSymbol).ToList(),
            second.Select(i => i.ExchangeSymbol).ToList());
    }

    [Fact]
    public void SeedTargetsTheKrakenExchange()
    {
        foreach (var instrument in MarketUniverseSeed.Create())
        {
            Assert.Equal("Kraken", instrument.ExchangeName, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Kraken names Bitcoin XBT and Dogecoin XDG. The seed must use the
    /// venue's own codes, because a symbol the exchange does not recognise
    /// would silently never appear in the catalogue.
    /// </summary>
    [Fact]
    public void SeedUsesKrakenAssetCodes()
    {
        Assert.Contains("XBT", MarketUniverseSeed.SeedBaseAssets, StringComparer.Ordinal);
        Assert.Contains("XDG", MarketUniverseSeed.SeedBaseAssets, StringComparer.Ordinal);
        Assert.DoesNotContain("BTC", MarketUniverseSeed.SeedBaseAssets, StringComparer.Ordinal);
        Assert.DoesNotContain("DOGE", MarketUniverseSeed.SeedBaseAssets, StringComparer.Ordinal);
    }
}
