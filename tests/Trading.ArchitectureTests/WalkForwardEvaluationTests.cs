using Trading.Optimization;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class WalkForwardEvaluationTests
{
    [Fact]
    public void WalkForwardFoldRejectsOverlappingValidationWindow()
    {
        var training = new DatasetSplit(
            "train-1",
            DatasetSplitType.Training,
            DatasetSplitTestFactory.Create("BTCUSDT"),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
            500);

        var validation = new DatasetSplit(
            "validate-1",
            DatasetSplitType.Validation,
            DatasetSplitTestFactory.Create("BTCUSDT"),
            new DateTimeOffset(2024, 1, 10, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 20, 0, 0, 0, TimeSpan.Zero),
            300);

        var parameters = new StrategyParameterSet(new[]
        {
            new StrategyParameterDefinition("lookback", 5m, 20m, 10m, "Lookback period")
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new WalkForwardFold("fold-1", training, validation, parameters, 0.75m));

        Assert.Contains("future-data leakage", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WalkForwardEvaluationResultOrdersAndAveragesFoldScores()
    {
        var definitions = new[]
        {
            new StrategyParameterDefinition("lookback", 5m, 20m, 10m, "Lookback period")
        };

        var foldA = new WalkForwardFold(
            "fold-a",
            new DatasetSplit(
                "train-a",
                DatasetSplitType.Training,
                DatasetSplitTestFactory.Create("ETHUSDT"),
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero),
                500),
            new DatasetSplit(
                "validate-a",
                DatasetSplitType.Validation,
                DatasetSplitTestFactory.Create("ETHUSDT"),
                new DateTimeOffset(2024, 1, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2024, 1, 31, 0, 0, 0, TimeSpan.Zero),
                300),
            new StrategyParameterSet(definitions),
            0.75m);

        var foldB = new WalkForwardFold(
            "fold-b",
            new DatasetSplit(
                "train-b",
                DatasetSplitType.Training,
                DatasetSplitTestFactory.Create("ETHUSDT"),
                new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2024, 2, 15, 0, 0, 0, TimeSpan.Zero),
                500),
            new DatasetSplit(
                "validate-b",
                DatasetSplitType.Validation,
                DatasetSplitTestFactory.Create("ETHUSDT"),
                new DateTimeOffset(2024, 2, 16, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2024, 3, 2, 0, 0, 0, TimeSpan.Zero),
                300),
            new StrategyParameterSet(definitions),
            0.90m);

        var report = new WalkForwardEvaluationResult(new[] { foldA, foldB });

        Assert.Equal(2, report.FoldCount);
        Assert.Equal(0.825m, report.AverageScore);
        Assert.Equal(foldB, report.BestFold);
        Assert.Contains("average objective", report.ToSummary(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReportAcceptsChronologicalExpandingAndRollingFoldsAndPreservesImmutableEvidence()
    {
        var expanding = Fold("expanding", 1, 10, 10, 15, 0.4m);
        var rolling = Fold("rolling", 10, 20, 20, 25, 0.8m);

        var report = new WalkForwardEvaluationResult(new[] { rolling, expanding });

        Assert.Collection(report.Folds, fold => Assert.Equal("expanding", fold.Id), fold => Assert.Equal("rolling", fold.Id));
        Assert.Equal(0.6m, report.AggregateMetrics.AverageObjective);
        Assert.Equal(0.4m, report.AggregateMetrics.MinimumObjective);
        Assert.Equal(0.8m, report.AggregateMetrics.MaximumObjective);
        Assert.Equal("decimal-objective", report.Folds[0].ObjectiveId);
        Assert.NotEmpty(report.Folds[0].ParameterIdentity);
        var folds = Assert.IsAssignableFrom<IList<WalkForwardFold>>(report.Folds);
        Assert.Throws<NotSupportedException>(() => folds.Add(expanding));
        Assert.All(typeof(WalkForwardFold).GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Fact]
    public void BestFoldUsesStableChronologicalAndOrdinalTieBreaking()
    {
        var later = Fold("z-fold", 10, 20, 20, 25, 1m);
        var earlier = Fold("a-fold", 1, 10, 10, 15, 1m);

        var report = new WalkForwardEvaluationResult(new[] { later, earlier });

        Assert.Equal("a-fold", report.BestFold.Id);
    }

    [Theory]
    [InlineData(10, 15, 14, 20)]
    public void ReportRejectsOverlappingOrReversedValidationWindows(int firstFrom, int firstTo, int secondFrom, int secondTo)
    {
        var first = Fold("first", 1, firstFrom, firstFrom, firstTo, 0.1m);
        var second = Fold("second", 1, secondFrom, secondFrom, secondTo, 0.2m);

        Assert.Throws<InvalidOperationException>(() => new WalkForwardEvaluationResult(new[] { first, second }));
    }

    [Fact]
    public void ReportRejectsCrossDatasetFoldAndMissingFoldInsteadOfIgnoringThem()
    {
        var valid = Fold("valid", 1, 10, 10, 15, 0.1m);
        var otherDataset = Fold("other", 15, 20, 20, 25, 0.2m, "ETHUSDT");

        Assert.Throws<ArgumentException>(() => new WalkForwardEvaluationResult(new[] { valid, otherDataset }));
        Assert.Throws<ArgumentException>(() => new WalkForwardEvaluationResult(new WalkForwardFold[] { valid, null! }));
    }

    [Fact]
    public void ReportRejectsDuplicateIdsAndExcessiveFoldCounts()
    {
        var duplicateA = Fold("duplicate", 1, 10, 10, 15, 0.1m);
        var duplicateB = Fold("duplicate", 10, 20, 20, 25, 0.2m);
        var excessive = Enumerable.Range(0, WalkForwardEvaluationResult.MaximumFoldCount + 1)
            .Select(index => Fold($"fold-{index}", 1, 10, 10 + (index * 2), 11 + (index * 2), index))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new WalkForwardEvaluationResult(new[] { duplicateA, duplicateB }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WalkForwardEvaluationResult(excessive));
    }

    [Fact]
    public void ReportRejectsMixedObjectiveIdentity()
    {
        var first = Fold("first", 1, 10, 10, 15, 0.1m);
        var second = new WalkForwardFold(
            "second",
            first.TrainingSplit,
            Split("second-validation", DatasetSplitType.Validation, DatasetSplitTestFactory.Create(), 15, 20),
            Parameters(),
            0.2m,
            "different-objective");

        Assert.Throws<ArgumentException>(() => new WalkForwardEvaluationResult(new[] { first, second }));
    }

    [Fact]
    public void OptimizationRunRejectsWalkForwardFoldThatTouchesFinalHoldout()
    {
        var dataset = DatasetSplitTestFactory.Create();
        var training = Split("training", DatasetSplitType.Training, dataset, 1, 10);
        var validation = Split("validation", DatasetSplitType.Validation, dataset, 10, 20);
        var holdout = Split("holdout", DatasetSplitType.Holdout, dataset, 30, 40);
        var report = new WalkForwardEvaluationResult(new[]
        {
            new WalkForwardFold(
                "touches-holdout",
                Split("walk-training", DatasetSplitType.Training, dataset, 20, 30),
                Split("walk-validation", DatasetSplitType.Validation, dataset, 30, 35),
                Parameters(),
                0.1m,
                "decimal-objective")
        });
        var run = new OptimizationRun("run", training, validation, holdout, Definitions());

        Assert.Throws<InvalidOperationException>(() => run.Execute((_, _) => 1m, walkForward: report));
        Assert.False(run.HoldoutVerified);
    }

    [Fact]
    public void EvaluationModelHasNoHttpExchangeOrExecutionDependencies()
    {
        var references = typeof(WalkForwardEvaluationResult).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.DoesNotContain(references, name =>
            name!.Contains("Http", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Exchange", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Execution", StringComparison.OrdinalIgnoreCase));
    }

    private static WalkForwardFold Fold(
        string id,
        int trainingFrom,
        int trainingTo,
        int validationFrom,
        int validationTo,
        decimal objective,
        string symbol = "BTCUSDT")
    {
        var dataset = DatasetSplitTestFactory.Create(symbol);
        return new WalkForwardFold(
            id,
            Split($"{id}-training", DatasetSplitType.Training, dataset, trainingFrom, trainingTo),
            Split($"{id}-validation", DatasetSplitType.Validation, dataset, validationFrom, validationTo),
            Parameters(),
            objective,
            "decimal-objective");
    }

    private static DatasetSplit Split(
        string id,
        DatasetSplitType splitType,
        Trading.Backtesting.HistoricalDataset dataset,
        int fromDay,
        int toDay) =>
        new(
            id,
            splitType,
            dataset,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(fromDay - 1),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(toDay - 1),
            10);

    private static StrategyParameterSet Parameters() => new(Definitions());

    private static StrategyParameterDefinition[] Definitions() =>
    [
        new StrategyParameterDefinition("lookback", 5m, 20m, 10m, "Lookback period")
    ];
}
