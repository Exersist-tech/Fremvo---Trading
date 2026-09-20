using Trading.Optimization;

namespace Trading.ArchitectureTests;

public sealed class OptimizationDatasetSplitTests
{
    [Fact]
    public void AdjacentEndExclusiveWindowsAreNonOverlappingAndOrdered()
    {
        var dataset = DatasetSplitTestFactory.Create();
        var training = Split("training", DatasetSplitType.Training, dataset, 1, 10);
        var validation = Split("validation", DatasetSplitType.Validation, dataset, 10, 20);

        Assert.True(training.IsNonOverlappingWith(validation));
        Assert.True(validation.IsTimeOrderedAfter(training));
        training.ValidateNoFutureLeakage(validation);
    }

    [Fact]
    public void OverlappingWindowsAreRejectedAsFutureLeakage()
    {
        var dataset = DatasetSplitTestFactory.Create();
        var training = Split("training", DatasetSplitType.Training, dataset, 1, 12);
        var validation = Split("validation", DatasetSplitType.Validation, dataset, 10, 20);

        Assert.False(training.IsNonOverlappingWith(validation));
        Assert.Throws<InvalidOperationException>(() => training.ValidateNoFutureLeakage(validation));
    }

    [Fact]
    public void SplitRangeMustBeContainedWithinItsDataset()
    {
        var dataset = DatasetSplitTestFactory.Create();

        Assert.Throws<ArgumentException>(() => new DatasetSplit(
            "outside",
            DatasetSplitType.Training,
            dataset,
            new DateTimeOffset(2023, 12, 31, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
            1));
    }

    [Fact]
    public void ConstructorRejectsBlankIdInvalidRangeAndNonUtcTimestamp()
    {
        var dataset = DatasetSplitTestFactory.Create();

        Assert.Throws<ArgumentException>(() => Split(" ", DatasetSplitType.Training, dataset, 1, 2));
        Assert.Throws<ArgumentException>(() => Split("inverted", DatasetSplitType.Training, dataset, 2, 1));
        Assert.Throws<ArgumentException>(() => new DatasetSplit(
            "local",
            DatasetSplitType.Training,
            dataset,
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero),
            1));
    }

    [Fact]
    public void CrossDatasetSourceSymbolAndIntervalComparisonsAreRejected()
    {
        var reference = Split("reference", DatasetSplitType.Training, DatasetSplitTestFactory.Create(), 1, 10);
        var changedSource = Split("source", DatasetSplitType.Validation, DatasetSplitTestFactory.Create(source: "other"), 10, 20);
        var changedSymbol = Split("symbol", DatasetSplitType.Validation, DatasetSplitTestFactory.Create(symbol: "ETHUSDT"), 10, 20);
        var changedInterval = Split("interval", DatasetSplitType.Validation, DatasetSplitTestFactory.Create(interval: "1H"), 10, 20);

        Assert.Throws<ArgumentException>(() => reference.IsNonOverlappingWith(changedSource));
        Assert.Throws<ArgumentException>(() => reference.IsNonOverlappingWith(changedSymbol));
        Assert.Throws<ArgumentException>(() => reference.IsNonOverlappingWith(changedInterval));
    }

    [Fact]
    public void DuplicateSemanticRangeIsDetectedAndRejectedAsAnOverlap()
    {
        var dataset = DatasetSplitTestFactory.Create();
        var first = Split("first", DatasetSplitType.Validation, dataset, 10, 20);
        var duplicate = Split("duplicate", DatasetSplitType.Validation, dataset, 10, 20);

        Assert.True(first.IsSameSemanticRangeAs(duplicate));
        Assert.Throws<InvalidOperationException>(() => first.ValidateNoFutureLeakage(duplicate));
    }

    [Fact]
    public void HoldoutIsImmutableAndNeverAvailableForSelection()
    {
        var holdout = Split("holdout", DatasetSplitType.Holdout, DatasetSplitTestFactory.Create(), 20, 30);

        Assert.True(holdout.IsLockedForEvaluation);
        Assert.False(holdout.IsAvailableForSelection);
        Assert.All(typeof(DatasetSplit).GetProperties(), property => Assert.False(property.CanWrite));
    }

    private static DatasetSplit Split(
        string id,
        DatasetSplitType type,
        Trading.Backtesting.HistoricalDataset dataset,
        int fromDay,
        int toDay) =>
        new(
            id,
            type,
            dataset,
            new DateTimeOffset(2024, 1, fromDay, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, toDay, 0, 0, 0, TimeSpan.Zero),
            1);
}
