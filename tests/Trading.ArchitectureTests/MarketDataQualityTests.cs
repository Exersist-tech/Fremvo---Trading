using Trading.MarketData;
using Trading.Domain.Market;

namespace Trading.ArchitectureTests;

public sealed class MarketDataQualityTests
{
    [Fact]
    public void CandleQualityEvaluatorFlagsIncompleteAndDerivedCandles()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candle = new Candle(
            "BTCUSDT",
            CandleInterval.OneMinute,
            now.AddMinutes(-1),
            now,
            100m,
            101m,
            99m,
            100.5m,
            10m,
            isClosed: false,
            isDerived: true);

        var issues = CandleQualityEvaluator.Evaluate(candle, null, now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

        Assert.Contains(DataQualityIssue.Incomplete, issues);
        Assert.Contains(DataQualityIssue.Derived, issues);
    }

    [Fact]
    public void CandleQualityEvaluatorDetectsOutOfOrderAndDuplicateCandles()
    {
        var now = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);
        var previous = new Candle(
            "BTCUSDT",
            CandleInterval.OneMinute,
            now.AddMinutes(-2),
            now.AddMinutes(-1),
            100m,
            101m,
            99m,
            100.5m,
            10m,
            isClosed: true,
            isDerived: false);

        var duplicate = new Candle(
            "BTCUSDT",
            CandleInterval.OneMinute,
            previous.OpenTimeUtc,
            previous.CloseTimeUtc,
            100m,
            101m,
            99m,
            100.5m,
            10m,
            isClosed: true,
            isDerived: false);

        var outOfOrder = new Candle(
            "BTCUSDT",
            CandleInterval.OneMinute,
            previous.OpenTimeUtc.AddMinutes(-1),
            previous.CloseTimeUtc.AddMinutes(-1),
            100m,
            101m,
            99m,
            100.5m,
            10m,
            isClosed: true,
            isDerived: false);

        var duplicateIssues = CandleQualityEvaluator.Evaluate(duplicate, previous, now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        var outOfOrderIssues = CandleQualityEvaluator.Evaluate(outOfOrder, previous, now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

        Assert.Contains(DataQualityIssue.Duplicate, duplicateIssues);
        Assert.Contains(DataQualityIssue.OutOfOrder, outOfOrderIssues);
    }
}
