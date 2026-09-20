using Trading.Application.Scanner;
using Trading.Domain.Market;
using Trading.Domain.Scanner;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class ScanCriteriaEvaluatorTests
{
    private static readonly DateTimeOffset s_asOf = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluatesKnownFixtureWithDecimalIndicatorEvidenceAndAndSemantics()
    {
        var request = Request(
            "BTC/USD",
            new ScanCriterion(ScanCriterionKind.MinimumCandleVolume, 1.25m),
            new ScanCriterion(ScanCriterionKind.CloseAboveSimpleMovingAverage, 3m));
        var evaluation = ScanCriteriaEvaluator.Evaluate(
            request, Guid.NewGuid(), s_asOf, s_asOf,
            new Dictionary<string, IReadOnlyList<Candle>>
            {
                ["BTC/USD"] = Candles("BTC/USD", 10m, 12m, 14.5m, volumes: [1m, 1m, 1.25m])
            });

        var symbol = Assert.Single(evaluation.Symbols);
        var result = Assert.Single(evaluation.Results);
        Assert.Equal(1m, result.Score);
        Assert.Equal(12.166666666666666666666666667m, symbol.Criteria.Single(x => x.Kind == ScanCriterionKind.CloseAboveSimpleMovingAverage).ObservedValue);
        Assert.All(symbol.Criteria, criterion => Assert.Equal(ScanCriterionOutcome.Passed, criterion.Outcome));

        var failingRequest = Request(
            "BTC/USD",
            new ScanCriterion(ScanCriterionKind.MinimumCandleVolume, 2m),
            new ScanCriterion(ScanCriterionKind.CloseAboveSimpleMovingAverage, 3m));
        var failing = ScanCriteriaEvaluator.Evaluate(
            failingRequest, Guid.NewGuid(), s_asOf, s_asOf,
            new Dictionary<string, IReadOnlyList<Candle>> { ["BTC/USD"] = Candles("BTC/USD", 10m, 12m, 14.5m) });
        Assert.Empty(failing.Results);
        Assert.Equal(ScanCriterionOutcome.Failed, Assert.Single(failing.Symbols).Criteria.Single(x => x.Kind == ScanCriterionKind.MinimumCandleVolume).Outcome);
    }

    [Fact]
    public void ReportsIndicatorWarmupAsUnavailable()
    {
        var request = Request("BTC/USD", new ScanCriterion(ScanCriterionKind.CloseAboveSimpleMovingAverage, 3m));
        var evaluation = ScanCriteriaEvaluator.Evaluate(
            request, Guid.NewGuid(), s_asOf, s_asOf,
            new Dictionary<string, IReadOnlyList<Candle>> { ["BTC/USD"] = Candles("BTC/USD", 10m, 12m) });

        var evidence = Assert.Single(Assert.Single(evaluation.Symbols).Criteria);
        Assert.Equal(ScanCriterionOutcome.Unavailable, evidence.Outcome);
        Assert.Contains("requires 3", evidence.Reason, StringComparison.Ordinal);
        Assert.Empty(evaluation.Results);
    }

    [Fact]
    public void ValidatesCriterionBoundaries()
    {
        Assert.NotNull(new ScanCriterion(ScanCriterionKind.CloseAboveSimpleMovingAverage, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanCriterion(ScanCriterionKind.CloseAboveSimpleMovingAverage, 1.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanCriterion(ScanCriterionKind.CloseAboveSimpleMovingAverage, 10_001m));
    }

    [Fact]
    public void RejectsUnsafeMissingDuplicateOutOfOrderAndUnclosedHistory()
    {
        var request = Request("BTC/USD", new ScanCriterion(ScanCriterionKind.MinimumCandleVolume, 1m));
        var scenarios = new IReadOnlyList<Candle>[]
        {
            [Candle("BTC/USD", -2, 1m), Candle("BTC/USD", 0, 1m)],
            [Candle("BTC/USD", -1, 1m), Candle("BTC/USD", -1, 1m), Candle("BTC/USD", 0, 1m)],
            [Candle("BTC/USD", 0, 1m), Candle("BTC/USD", -1, 1m)],
            [Candle("BTC/USD", -1, 1m, isClosed: false), Candle("BTC/USD", 0, 1m)]
        };

        foreach (var candles in scenarios)
        {
            var evaluation = ScanCriteriaEvaluator.Evaluate(
                request, Guid.NewGuid(), s_asOf, s_asOf,
                new Dictionary<string, IReadOnlyList<Candle>> { ["BTC/USD"] = candles });
            var symbol = Assert.Single(evaluation.Symbols);
            Assert.NotNull(symbol.RejectionReason);
            Assert.All(symbol.Criteria, criterion => Assert.Equal(ScanCriterionOutcome.Rejected, criterion.Outcome));
            Assert.Empty(evaluation.Results);
        }
    }

    [Fact]
    public void RejectsFutureEvidenceInsteadOfSilentlyUsingIt()
    {
        var request = Request("BTC/USD", new ScanCriterion(ScanCriterionKind.CloseAboveSimpleMovingAverage, 2m));
        var baseline = Candles("BTC/USD", 10m, 12m);
        var accepted = ScanCriteriaEvaluator.Evaluate(request, Guid.NewGuid(), s_asOf, s_asOf,
            new Dictionary<string, IReadOnlyList<Candle>> { ["BTC/USD"] = baseline });
        var future = baseline.Append(Candle("BTC/USD", 1, 1_000m)).ToArray();
        var rejected = ScanCriteriaEvaluator.Evaluate(request, Guid.NewGuid(), s_asOf, s_asOf,
            new Dictionary<string, IReadOnlyList<Candle>> { ["BTC/USD"] = future });

        Assert.Single(accepted.Results);
        Assert.Empty(rejected.Results);
        Assert.Contains("future", Assert.Single(rejected.Symbols).RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RanksTiesStablyBySymbolAndHonoursLimit()
    {
        var request = Request(
            ["ETH/USD", "BTC/USD", "SOL/USD"],
            2,
            new ScanCriterion(ScanCriterionKind.MinimumCandleVolume, 1m));
        var data = new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["BTC/USD"] = Candles("BTC/USD", 2m),
            ["ETH/USD"] = Candles("ETH/USD", 2m),
            ["SOL/USD"] = Candles("SOL/USD", 2m)
        };

        var first = ScanCriteriaEvaluator.Evaluate(request, Guid.NewGuid(), s_asOf, s_asOf, data);
        var second = ScanCriteriaEvaluator.Evaluate(request, Guid.NewGuid(), s_asOf, s_asOf, data);
        Assert.Equal(["BTC/USD", "ETH/USD"], first.Results.Select(result => result.Symbol));
        Assert.Equal(first.Results.Select(result => result.Symbol), second.Results.Select(result => result.Symbol));
        Assert.Equal([1, 2], first.Results.Select(result => result.Rank));
    }

    private static ScanRequest Request(string symbol, params ScanCriterion[] criteria) =>
        Request([symbol], 10, criteria);

    private static ScanRequest Request(IEnumerable<string> symbols, int limit, params ScanCriterion[] criteria) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Test", symbols, CandleInterval.OneHour, criteria, limit, s_asOf);

    private static Candle[] Candles(string symbol, params decimal[] closes) =>
        Candles(symbol, closes, null);

    private static Candle[] Candles(string symbol, decimal first, decimal second, decimal third, decimal[] volumes) =>
        Candles(symbol, [first, second, third], volumes);

    private static Candle[] Candles(string symbol, decimal[] closes, decimal[]? volumes)
    {
        var start = s_asOf.AddHours(-closes.Length);
        return closes.Select((close, index) => new Candle(
            symbol, CandleInterval.OneHour, start.AddHours(index), start.AddHours(index + 1),
            close, close, close, close, volumes?[index] ?? 1m, true, false)).ToArray();
    }

    private static Candle Candle(string symbol, int offset, decimal close, bool isClosed = true) =>
        new(symbol, CandleInterval.OneHour, s_asOf.AddHours(offset - 1), s_asOf.AddHours(offset),
            close, close, close, close, 1m, isClosed, false);
}
