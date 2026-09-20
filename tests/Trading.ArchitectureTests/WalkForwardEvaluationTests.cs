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
        Assert.Contains("average score", report.ToSummary(), StringComparison.OrdinalIgnoreCase);
    }
}
