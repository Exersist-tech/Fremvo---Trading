using Trading.Optimization;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class OptimizationRunTests
{
    private static readonly StrategyParameterDefinition[] Definitions =
    {
        new("lookback", 5m, 20m, 10m, "Lookback period")
    };

    private static DatasetSplit Split(string id, DatasetSplitType type, int startDay, int endDay, string symbol = "BTCUSDT") =>
        new(
            id,
            type,
            symbol,
            new DateTimeOffset(2024, 1, startDay, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, endDay, 0, 0, 0, TimeSpan.Zero),
            100);

    private static OptimizationRun CreateRun() =>
        new(
            "run-1",
            Split("train", DatasetSplitType.Training, 1, 10),
            Split("validate", DatasetSplitType.Validation, 10, 20),
            Split("holdout", DatasetSplitType.Holdout, 20, 30),
            Definitions);

    [Fact]
    public void OptimizationRunSelectsOnValidationAndScoresHoldoutExactlyOnce()
    {
        var run = CreateRun();
        var holdoutEvaluations = 0;

        var result = run.Execute((parameters, split) =>
        {
            if (split.SplitType == DatasetSplitType.Holdout)
            {
                holdoutEvaluations++;
            }

            return parameters.Get("lookback");
        }, gridPointsPerParameter: 4);

        Assert.Equal(1, holdoutEvaluations);
        Assert.True(run.HoldoutVerified);
        Assert.Equal(20m, result.SelectedParameters.Get("lookback"));
        Assert.Equal(20m, result.ValidationScore);
        Assert.Equal(20m, result.HoldoutScore);
        Assert.Contains("do not guarantee profit", OptimizationRunResult.NoGuaranteeDisclaimer, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.RankedCandidates);
    }

    [Fact]
    public void OptimizationRunCannotBeExecutedTwice()
    {
        var run = CreateRun();
        run.Execute((parameters, _) => parameters.Get("lookback"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            run.Execute((parameters, _) => parameters.Get("lookback")));

        Assert.Contains("already verified", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptimizationRunRejectsHoldoutThatPrecedesValidation()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OptimizationRun(
            "run-bad",
            Split("train", DatasetSplitType.Training, 1, 5),
            Split("validate", DatasetSplitType.Validation, 20, 30),
            Split("holdout", DatasetSplitType.Holdout, 5, 20),
            Definitions));

        Assert.Contains("look-ahead", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptimizationRunRejectsOverlappingSplits()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new OptimizationRun(
            "run-overlap",
            Split("train", DatasetSplitType.Training, 1, 12),
            Split("validate", DatasetSplitType.Validation, 10, 20),
            Split("holdout", DatasetSplitType.Holdout, 20, 30),
            Definitions));

        Assert.Contains("non-overlapping", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptimizationRunRejectsMixedSymbols()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OptimizationRun(
            "run-symbols",
            Split("train", DatasetSplitType.Training, 1, 10),
            Split("validate", DatasetSplitType.Validation, 10, 20),
            Split("holdout", DatasetSplitType.Holdout, 20, 30, "ETHUSDT"),
            Definitions));

        Assert.Equal("holdout", ex.ParamName);
    }

    [Fact]
    public void DatasetSplitIsTimeOrderedAfterDetectsOverlap()
    {
        var earlier = Split("train", DatasetSplitType.Training, 1, 12);
        var later = Split("validate", DatasetSplitType.Validation, 10, 20);

        Assert.False(later.IsTimeOrderedAfter(earlier));
        Assert.True(Split("holdout", DatasetSplitType.Holdout, 20, 30).IsTimeOrderedAfter(later));
    }
}
