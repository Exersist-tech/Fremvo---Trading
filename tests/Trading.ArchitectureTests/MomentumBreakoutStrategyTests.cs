using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class MomentumBreakoutStrategyTests
{
    private static readonly DateTimeOffset s_start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluatesKnownBreakoutAndNonBreakoutFixtures()
    {
        var strategy = new MomentumBreakoutStrategyTemplate();

        var breakout = strategy.Evaluate(Input(strategy, Candles(100m, 101m, 102m, 103m, 104m, 105m, 107m)));
        var nonBreakout = strategy.Evaluate(Input(strategy, Candles(100m, 101m, 102m, 103m, 104m, 105m, 105m)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, breakout.Direction);
        Assert.Equal(0.55m, breakout.Confidence);
        Assert.Contains("analysis only", breakout.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StrategyAnalysisDirection.Neutral, nonBreakout.Direction);
        Assert.Contains("No bullish breakout", nonBreakout.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void MeetsExactBreakoutAndVolumeBoundaries()
    {
        var strategy = new MomentumBreakoutStrategyTemplate();
        var parameters = Parameters(strategy, threshold: 1m, volumeMultiplier: 1.2m);
        var candles = Candles(100m, 100m, 100m, 100m, 100m, 100m, 101m, candidateVolume: 12m);

        var result = strategy.Evaluate(Input(strategy, candles, parameters));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
    }

    [Fact]
    public void ReturnsUnavailableNeutralProposalForInsufficientHistory()
    {
        var strategy = new MomentumBreakoutStrategyTemplate();

        var result = strategy.Evaluate(Input(strategy, Candles(100m, 101m, 102m, 103m, 104m, 105m)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Equal(0m, result.Confidence);
        Assert.Contains("Unavailable", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractRejectsUnclosedUnsafeAndOutOfOrderCandlesBeforeEvaluation()
    {
        var strategy = new MomentumBreakoutStrategyTemplate();
        var valid = CandleAt(0, 100m, 10m);
        var unclosed = new Candle(
            "BTCUSD", CandleInterval.OneMinute, s_start.AddMinutes(1), s_start.AddMinutes(2),
            100m, 100m, 99m, 100m, 10m, false, false);
        var unsafeCandle = new Candle(
            "BTCUSD", CandleInterval.OneMinute, s_start.AddMinutes(1), s_start.AddMinutes(2),
            100m, 100m, 99m, 100m, 10m, true, false, [DataQualityIssue.Stale]);

        Assert.Throws<ArgumentException>(() => Input(strategy, [valid, unclosed]));
        Assert.Throws<ArgumentException>(() => Input(strategy, [valid, unsafeCandle]));
        Assert.Throws<ArgumentException>(() => Input(strategy, [CandleAt(1, 100m, 10m), valid]));
    }

    [Fact]
    public void TargetEvaluationHasNoFutureViewAndDoesNotMutateState()
    {
        var strategy = new MomentumBreakoutStrategyTemplate();
        var targetCandles = Candles(100m, 101m, 102m, 103m, 104m, 105m, 107m);
        var state = new StrategyState(strategy.TemplateId, 4, targetCandles[0].CloseTimeUtc, new Dictionary<string, decimal> { ["marker"] = 7m });
        var targetInput = Input(strategy, targetCandles, state: state);
        var target = strategy.Evaluate(targetInput);
        var laterInput = Input(strategy, [.. targetCandles, CandleAt(7, 200m, 30m)], state: state);
        var later = strategy.Evaluate(laterInput);

        Assert.Equal(targetCandles[^1].CloseTimeUtc, target.ObservedAtUtc);
        Assert.DoesNotContain(targetInput.ClosedCandles, candle => candle.CloseTimeUtc > targetInput.AsOfUtc);
        Assert.Equal(4, state.Revision);
        Assert.Equal(7m, state.Values["marker"]);
        Assert.NotEqual(target.ObservedAtUtc, later.ObservedAtUtc);
    }

    [Fact]
    public void IsDeterministicAndDoesNotRepeatAnOngoingBreakout()
    {
        var strategy = new MomentumBreakoutStrategyTemplate();
        var candles = new[]
        {
            CandleAt(0, 100m, 10m),
            CandleAt(1, 100m, 10m),
            CandleAt(2, 100m, 10m),
            CandleAt(3, 100m, 10m),
            CandleAt(4, 100m, 10m),
            CandleAt(5, 100m, 10m),
            CandleAt(6, 101m, 12m),
            CandleAt(7, 103m, 13m)
        };
        var input = Input(strategy, candles);

        var first = strategy.Evaluate(input);
        var second = strategy.Evaluate(input);

        Assert.Equal(StrategyAnalysisDirection.Neutral, first.Direction);
        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.Contains("No new breakout edge", first.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsForeignOrOutOfBoundsParameterDefinitions()
    {
        var strategy = new MomentumBreakoutStrategyTemplate();
        var outOfBounds = new Dictionary<string, StrategyParameterValue>
        {
            ["lookbackPeriods"] = StrategyParameterValue.WholeNumber(4m),
            ["breakoutThresholdPercent"] = StrategyParameterValue.FromNumeric(1m),
            ["volumeMultiplier"] = StrategyParameterValue.FromNumeric(1.2m)
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(strategy.ParameterDefinitions, outOfBounds));

        var foreignBounds = new[]
        {
            new StrategyParameterDefinition("lookbackPeriods", 2m, 100m, 20m, "Foreign.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition("breakoutThresholdPercent", 0.5m, 3m, 1m, "Foreign."),
            new StrategyParameterDefinition("volumeMultiplier", 1m, 3m, 1.2m, "Foreign.")
        };

        Assert.Throws<ArgumentException>(() => strategy.Evaluate(Input(
            strategy,
            Candles(100m, 101m, 102m, 103m, 104m, 105m, 107m),
            new StrategyParameterSet(foreignBounds))));
    }

    private static StrategyEvaluationInput Input(
        MomentumBreakoutStrategyTemplate strategy,
        Candle[] candles,
        StrategyParameterSet? parameters = null,
        StrategyState? state = null) =>
        new(
            strategy.TemplateId,
            parameters ?? Parameters(strategy),
            state ?? new StrategyState(strategy.TemplateId, 0, candles[0].CloseTimeUtc),
            candles);

    private static StrategyParameterSet Parameters(
        MomentumBreakoutStrategyTemplate strategy,
        decimal threshold = 1m,
        decimal volumeMultiplier = 1.2m) =>
        new(
            strategy.ParameterDefinitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["lookbackPeriods"] = StrategyParameterValue.WholeNumber(5m),
                ["breakoutThresholdPercent"] = StrategyParameterValue.FromNumeric(threshold),
                ["volumeMultiplier"] = StrategyParameterValue.FromNumeric(volumeMultiplier)
            });

    private static Candle[] Candles(params decimal[] closes)
    {
        var candles = new Candle[closes.Length];
        for (var index = 0; index < closes.Length; index++)
        {
            candles[index] = CandleAt(index, closes[index], index == closes.Length - 1 ? 12m : 10m);
        }

        return candles;
    }

    private static Candle[] Candles(
        decimal first,
        decimal second,
        decimal third,
        decimal fourth,
        decimal fifth,
        decimal sixth,
        decimal seventh,
        decimal candidateVolume) =>
    [
        CandleAt(0, first, 10m),
        CandleAt(1, second, 10m),
        CandleAt(2, third, 10m),
        CandleAt(3, fourth, 10m),
        CandleAt(4, fifth, 10m),
        CandleAt(5, sixth, 10m),
        CandleAt(6, seventh, candidateVolume)
    ];

    private static Candle CandleAt(int minute, decimal close, decimal volume) =>
        new(
            "BTCUSD",
            CandleInterval.OneMinute,
            s_start.AddMinutes(minute),
            s_start.AddMinutes(minute + 1),
            close,
            close,
            close - 1m,
            close,
            volume,
            true,
            false);
}
