using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class DerivedCandleTests
{
    [Fact]
    public void TenMinuteCandleCanBeDerivedFromTenOneMinuteCandles()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var oneMinuteCandles = Enumerable.Range(0, 10)
            .Select(i => new Candle(
                "BTCUSDT",
                CandleInterval.OneMinute,
                start.AddMinutes(i),
                start.AddMinutes(i + 1),
                100m + i,
                101m + i,
                99m + i,
                100.5m + i,
                2m,
                isClosed: true,
                isDerived: false))
            .ToArray();

        var derived = DerivedCandleBuilder.BuildTenMinuteCandle(oneMinuteCandles, "BTCUSDT", start, start.AddMinutes(10), true);

        Assert.Equal(CandleInterval.TenMinutes, derived.Interval);
        Assert.True(derived.IsDerived);
        Assert.Equal(100m, derived.Open);
        Assert.Equal(110m, derived.High);
        Assert.Equal(99m, derived.Low);
        Assert.Equal(109.5m, derived.Close);
        Assert.Equal(20m, derived.Volume);
        Assert.Contains(DataQualityIssue.Derived, derived.QualityFlags);
    }

    [Fact]
    public void DerivedCandleRequiresExactlyTenOneMinuteCandles()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candles = new[]
        {
            new Candle("BTCUSDT", CandleInterval.OneMinute, start, start.AddMinutes(1), 100m, 101m, 99m, 100.5m, 1m, true, false),
            new Candle("BTCUSDT", CandleInterval.OneMinute, start.AddMinutes(1), start.AddMinutes(2), 100m, 101m, 99m, 100.5m, 1m, true, false)
        };

        Assert.Throws<ArgumentException>(() => DerivedCandleBuilder.BuildTenMinuteCandle(candles, "BTCUSDT", start, start.AddMinutes(2), true));
    }
}
