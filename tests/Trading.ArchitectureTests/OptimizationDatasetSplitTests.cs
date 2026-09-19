using Trading.Optimization;

namespace Trading.ArchitectureTests;

public sealed class OptimizationDatasetSplitTests
{
    [Fact]
    public void DatasetSplitRejectsOverlappingTimeWindows()
    {
        var training = new DatasetSplit(
            "train",
            DatasetSplitType.Training,
            "BTCUSDT",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero),
            1000);

        var validation = new DatasetSplit(
            "validate",
            DatasetSplitType.Validation,
            "BTCUSDT",
            new DateTimeOffset(2026, 1, 8, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            500);

        Assert.False(training.IsNonOverlappingWith(validation));
        var ex = Assert.Throws<InvalidOperationException>(() => training.ValidateNoFutureLeakage(validation));
        Assert.Contains("non-overlapping", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HoldoutSplitCanBeLockedForEvaluation()
    {
        var holdout = new DatasetSplit(
            "holdout",
            DatasetSplitType.Holdout,
            "ETHUSDT",
            new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero),
            500,
            isLockedForEvaluation: false);

        holdout.LockForEvaluation();
        Assert.True(holdout.IsLockedForEvaluation);
    }

    [Fact]
    public void NonHoldoutSplitCannotBeLockedForEvaluation()
    {
        var validation = new DatasetSplit(
            "validate",
            DatasetSplitType.Validation,
            "ETHUSDT",
            new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
            400);

        var ex = Assert.Throws<InvalidOperationException>(() => validation.LockForEvaluation());
        Assert.Contains("Only holdout splits can be locked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
