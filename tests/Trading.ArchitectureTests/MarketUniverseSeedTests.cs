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
    public void EverySeedSymbolIsQuotedInUsdt()
    {
        foreach (var symbol in MarketUniverseSeed.SeedSymbols)
        {
            Assert.EndsWith("USDT", symbol, StringComparison.Ordinal);
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
        // "G", "ONE" and "AR" are genuine short tickers, not leveraged
        // products, and must not be excluded by the leveraged-token rule.
        var classifier = MarketUniverseSeed.CreateClassifier();

        Assert.Equal(AssetClass.Cryptocurrency, classifier.Classify("G"));
        Assert.Equal(AssetClass.Cryptocurrency, classifier.Classify("ONE"));
        Assert.Equal(AssetClass.Cryptocurrency, classifier.Classify("AR"));
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
    public void SeedTargetsTheBinanceExchange()
    {
        foreach (var instrument in MarketUniverseSeed.Create())
        {
            Assert.Equal("Binance", instrument.ExchangeName, StringComparer.Ordinal);
        }
    }
}
