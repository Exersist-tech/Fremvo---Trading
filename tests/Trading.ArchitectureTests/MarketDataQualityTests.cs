using Trading.MarketData;
using Trading.Domain.Market;

namespace Trading.ArchitectureTests;

public sealed class MarketDataQualityTests
{
    private static readonly DateTimeOffset s_now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public void CandleQualityEvaluatorClassifiesLateStaleAndFutureClosedBarsAsUnsafe()
    {
        var late = CreateCandle(s_now.AddMinutes(-3), isClosed: true);
        var stale = CreateCandle(s_now.AddMinutes(-7), isClosed: true);
        var future = CreateCandle(s_now, isClosed: true);

        var lateIssues = CandleQualityEvaluator.Evaluate(late, null, s_now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        var staleIssues = CandleQualityEvaluator.Evaluate(stale, null, s_now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        var futureIssues = CandleQualityEvaluator.Evaluate(future, null, s_now, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));

        Assert.Contains(DataQualityIssue.Late, lateIssues);
        Assert.Contains(DataQualityIssue.Late, staleIssues);
        Assert.Contains(DataQualityIssue.Stale, staleIssues);
        Assert.Contains(DataQualityIssue.Incomplete, futureIssues);
    }

    [Fact]
    public void SequenceEvaluationFindsGapsWithoutHidingDuplicateOrOutOfOrderEvidence()
    {
        var sequence = new[]
        {
            CreateCandle(s_now.AddMinutes(-6)),
            CreateCandle(s_now.AddMinutes(-3)),
            CreateCandle(s_now.AddMinutes(-3)),
            CreateCandle(s_now.AddMinutes(-5)),
        };

        var issues = CandleQualityEvaluator.EvaluateSequence(
            sequence, s_now, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));

        Assert.Contains(DataQualityIssue.Missing, issues[1]);
        Assert.Contains(DataQualityIssue.Duplicate, issues[2]);
        Assert.Contains(DataQualityIssue.OutOfOrder, issues[3]);
    }

    [Theory]
    [InlineData(DataQualityIssue.Missing)]
    [InlineData(DataQualityIssue.Duplicate)]
    [InlineData(DataQualityIssue.Stale)]
    [InlineData(DataQualityIssue.Late)]
    [InlineData(DataQualityIssue.OutOfOrder)]
    [InlineData(DataQualityIssue.Incomplete)]
    public void SignalEligibilityFailsClosedForEveryBlockingQualityIssue(DataQualityIssue issue)
    {
        var candle = CreateCandle(s_now.AddMinutes(-1), isClosed: true, qualityFlags: new[] { issue });

        Assert.False(candle.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public void FormingAndDerivedEvidenceAreImmutableAndNoLookAheadSafe()
    {
        var inputFlags = new[] { DataQualityIssue.None };
        var forming = CreateCandle(s_now, isClosed: false, qualityFlags: inputFlags);
        inputFlags[0] = DataQualityIssue.Duplicate;

        Assert.Contains(DataQualityIssue.Incomplete, forming.QualityFlags);
        Assert.DoesNotContain(DataQualityIssue.Duplicate, forming.QualityFlags);
        Assert.False(forming.CanBeUsedForClosedCandleSignal);
        Assert.True(CreateCandle(s_now.AddMinutes(-1), isClosed: true, isDerived: true).CanBeUsedForClosedCandleSignal);
    }

    private static Candle CreateCandle(
        DateTimeOffset openTime,
        bool isClosed = true,
        bool isDerived = false,
        IReadOnlyCollection<DataQualityIssue>? qualityFlags = null) =>
        new(
            "BTC/USD", CandleInterval.OneMinute, openTime, openTime.AddMinutes(1),
            100m, 101m, 99m, 100m, 1m, isClosed, isDerived, qualityFlags);
}
