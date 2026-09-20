using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class MeanReversionStrategyTests
{
    private static readonly DateTimeOffset s_start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluatesKnownMeanReversionAndNoConditionFixtures()
    {
        var strategy = new MeanReversionStrategyTemplate();

        var condition = strategy.Evaluate(Input(strategy, Candles(100m, 100m, 100m, 100m, 100m, 100m, 98m)));
        var noCondition = strategy.Evaluate(Input(strategy, Candles(100m, 100m, 100m, 100m, 100m, 100m, 99m)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, condition.Direction);
        Assert.Equal(0.55m, condition.Confidence);
        Assert.Contains("analysis only", condition.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StrategyAnalysisDirection.Neutral, noCondition.Direction);
        Assert.Contains("No confirmed mean-reversion", noCondition.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void MeetsExactDeviationAndVolumeBoundaries()
    {
        var strategy = new MeanReversionStrategyTemplate();
        var parameters = Parameters(strategy, deviationThreshold: 2m, volumeMultiplier: 1.2m);
        var candles = Candles(100m, 100m, 100m, 100m, 100m, 100m, 98m, candidateVolume: 12m);

        var result = strategy.Evaluate(Input(strategy, candles, parameters));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
    }

    [Fact]
    public void ReturnsUnavailableNeutralProposalForInsufficientHistory()
    {
        var strategy = new MeanReversionStrategyTemplate();

        var result = strategy.Evaluate(Input(strategy, Candles(100m, 100m, 100m, 100m, 100m, 98m)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Equal(0m, result.Confidence);
        Assert.Contains("Unavailable", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractRejectsUnclosedStaleGappedUnsafeAndOutOfOrderCandlesBeforeEvaluation()
    {
        var strategy = new MeanReversionStrategyTemplate();
        var valid = CandleAt(0, 100m, 10m);
        var unclosed = new Candle(
            "BTCUSD", CandleInterval.OneMinute, s_start.AddMinutes(1), s_start.AddMinutes(2),
            100m, 100m, 99m, 100m, 10m, false, false);
        var stale = UnsafeCandleAt(1, DataQualityIssue.Stale);
        var gapped = UnsafeCandleAt(1, DataQualityIssue.Missing);
        var unsafeCandle = new Candle(
            "BTCUSD", CandleInterval.OneMinute, s_start.AddMinutes(1), s_start.AddMinutes(2),
            100m, 100m, 99m, 100m, 10m, true, false, [DataQualityIssue.Late]);

        Assert.Throws<ArgumentException>(() => Input(strategy, [valid, unclosed]));
        Assert.Throws<ArgumentException>(() => Input(strategy, [valid, stale]));
        Assert.Throws<ArgumentException>(() => Input(strategy, [valid, gapped]));
        Assert.Throws<ArgumentException>(() => Input(strategy, [valid, unsafeCandle]));
        Assert.Throws<ArgumentException>(() => Input(strategy, [CandleAt(1, 100m, 10m), valid]));
    }

    [Fact]
    public void TargetEvaluationHasNoFutureViewAndDoesNotMutateState()
    {
        var strategy = new MeanReversionStrategyTemplate();
        var targetCandles = Candles(100m, 100m, 100m, 100m, 100m, 100m, 98m);
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
    public void IsDeterministicResetSafeAndDoesNotRepeatAnOngoingCondition()
    {
        var strategy = new MeanReversionStrategyTemplate();
        var candles = new[]
        {
            CandleAt(0, 100m, 10m),
            CandleAt(1, 100m, 10m),
            CandleAt(2, 100m, 10m),
            CandleAt(3, 100m, 10m),
            CandleAt(4, 100m, 10m),
            CandleAt(5, 100m, 10m),
            CandleAt(6, 98m, 12m),
            CandleAt(7, 96m, 13m)
        };
        var input = Input(strategy, candles);

        var first = strategy.Evaluate(input);
        var second = strategy.Evaluate(input);
        var afterReset = new MeanReversionStrategyTemplate().Evaluate(input);

        Assert.Equal(StrategyAnalysisDirection.Neutral, first.Direction);
        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.Equal(first.Rationale, afterReset.Rationale);
        Assert.Contains("No new mean-reversion edge", first.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsForeignOrOutOfBoundsParameterDefinitions()
    {
        var strategy = new MeanReversionStrategyTemplate();
        var outOfBounds = new Dictionary<string, StrategyParameterValue>
        {
            ["lookbackPeriods"] = StrategyParameterValue.WholeNumber(4m),
            ["deviationThresholdPercent"] = StrategyParameterValue.FromNumeric(2m),
            ["volumeMultiplier"] = StrategyParameterValue.FromNumeric(1.2m)
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(strategy.ParameterDefinitions, outOfBounds));

        var foreignBounds = new[]
        {
            new StrategyParameterDefinition("lookbackPeriods", 2m, 100m, 20m, "Foreign.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition("deviationThresholdPercent", 0.5m, 10m, 2m, "Foreign."),
            new StrategyParameterDefinition("volumeMultiplier", 1m, 3m, 1.2m, "Foreign.")
        };

        Assert.Throws<ArgumentException>(() => strategy.Evaluate(Input(
            strategy,
            Candles(100m, 100m, 100m, 100m, 100m, 100m, 98m),
            new StrategyParameterSet(foreignBounds))));
    }

    [Fact]
    public void DefinesNoSizingAveragingOrExecutionBehavior()
    {
        var strategy = new MeanReversionStrategyTemplate();
        var parameterNames = strategy.ParameterDefinitions.Select(definition => definition.Name);
        var proposalProperties = typeof(StrategyAnalysisProposal).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain(parameterNames, name =>
            name.Contains("size", StringComparison.OrdinalIgnoreCase)
            || name.Contains("quantity", StringComparison.OrdinalIgnoreCase)
            || name.Contains("average", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(proposalProperties, name =>
            name.Contains("order", StringComparison.OrdinalIgnoreCase)
            || name.Contains("execution", StringComparison.OrdinalIgnoreCase)
            || name.Contains("quantity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("analysis only", strategy.Evaluate(Input(
            strategy,
            Candles(100m, 100m, 100m, 100m, 100m, 100m, 98m))).Rationale,
            StringComparison.OrdinalIgnoreCase);
    }

    private static StrategyEvaluationInput Input(
        MeanReversionStrategyTemplate strategy,
        Candle[] candles,
        StrategyParameterSet? parameters = null,
        StrategyState? state = null) =>
        new(
            strategy.TemplateId,
            parameters ?? Parameters(strategy),
            state ?? new StrategyState(strategy.TemplateId, 0, candles[0].CloseTimeUtc),
            candles);

    private static StrategyParameterSet Parameters(
        MeanReversionStrategyTemplate strategy,
        decimal deviationThreshold = 2m,
        decimal volumeMultiplier = 1.2m) =>
        new(
            strategy.ParameterDefinitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["lookbackPeriods"] = StrategyParameterValue.WholeNumber(5m),
                ["deviationThresholdPercent"] = StrategyParameterValue.FromNumeric(deviationThreshold),
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

    private static Candle UnsafeCandleAt(int minute, DataQualityIssue issue) =>
        new(
            "BTCUSD",
            CandleInterval.OneMinute,
            s_start.AddMinutes(minute),
            s_start.AddMinutes(minute + 1),
            100m,
            100m,
            99m,
            100m,
            10m,
            true,
            false,
            [issue]);

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
