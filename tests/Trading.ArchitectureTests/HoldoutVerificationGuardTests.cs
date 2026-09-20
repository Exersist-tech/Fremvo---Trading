using Trading.Optimization;

namespace Trading.ArchitectureTests;

public sealed class HoldoutVerificationGuardTests
{
    [Fact]
    public void GuardRejectsSelectionOverlapWithHoldout()
    {
        var holdout = new DatasetSplit(
            "holdout",
            DatasetSplitType.Holdout,
            DatasetSplitTestFactory.Create("BTCUSDT"),
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero),
            200);

        var guard = new HoldoutVerificationGuard(holdout);
        var training = new DatasetSplit(
            "train",
            DatasetSplitType.Training,
            DatasetSplitTestFactory.Create("BTCUSDT"),
            new DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 7, 0, 0, 0, TimeSpan.Zero),
            500);

        var ex = Assert.Throws<InvalidOperationException>(() => guard.RegisterSelectionCandidate(training));
        Assert.Contains("overlap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuardAllowsSingleHoldoutVerification()
    {
        var holdout = new DatasetSplit(
            "holdout",
            DatasetSplitType.Holdout,
            DatasetSplitTestFactory.Create("ETHUSDT"),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero),
            200);

        var guard = new HoldoutVerificationGuard(holdout);
        guard.VerifyOnce();

        Assert.True(guard.IsVerified);

        var ex = Assert.Throws<InvalidOperationException>(() => guard.VerifyOnce());
        Assert.Contains("already been performed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuardRejectsHoldoutAsASelectionCandidate()
    {
        var holdout = new DatasetSplit(
            "holdout",
            DatasetSplitType.Holdout,
            DatasetSplitTestFactory.Create("BTCUSDT"),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero),
            200);

        var guard = new HoldoutVerificationGuard(holdout);
        var ex = Assert.Throws<InvalidOperationException>(() => guard.RegisterSelectionCandidate(holdout));

        Assert.Contains("cannot be used for parameter selection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
