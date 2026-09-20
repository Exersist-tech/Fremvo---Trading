using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class MarketDataDomainTests
{
    [Fact]
    public void MarketSymbolRequiresConcreteIdentifiers()
    {
        var symbol = new MarketSymbol("BTCUSDT", "BTC", "USDT", true, "Kraken");

        Assert.Equal("BTCUSDT", symbol.Symbol);
        Assert.Equal("BTC", symbol.BaseAsset);
        Assert.Equal("USDT", symbol.QuoteAsset);
        Assert.True(symbol.IsActive);
    }

    [Fact]
    public void CandleRejectsUnclosedAndIncompleteInputs()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var candle = new Candle(
            "BTCUSDT",
            CandleInterval.OneMinute,
            now.AddMinutes(-1),
            now,
            100m,
            110m,
            95m,
            105m,
            12.5m,
            true,
            false,
            new[] { DataQualityIssue.None });

        Assert.True(candle.CanBeUsedForClosedCandleSignal);

        var incomplete = new Candle(
            "BTCUSDT",
            CandleInterval.FiveMinutes,
            now.AddMinutes(-5),
            now,
            100m,
            110m,
            95m,
            105m,
            12.5m,
            false,
            false,
            new[] { DataQualityIssue.Incomplete });

        Assert.False(incomplete.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public void DataQualityFlagsTrackMarketDataProblems()
    {
        Assert.Equal(DataQualityIssue.Missing, Enum.Parse<DataQualityIssue>("Missing"));
        Assert.Equal(DataQualityIssue.Duplicate, Enum.Parse<DataQualityIssue>("Duplicate"));
        Assert.Equal(DataQualityIssue.Late, Enum.Parse<DataQualityIssue>("Late"));
        Assert.Equal(DataQualityIssue.OutOfOrder, Enum.Parse<DataQualityIssue>("OutOfOrder"));
        Assert.Equal(DataQualityIssue.Derived, Enum.Parse<DataQualityIssue>("Derived"));
    }
}
