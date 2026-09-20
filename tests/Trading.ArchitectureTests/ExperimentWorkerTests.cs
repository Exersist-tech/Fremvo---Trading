using Trading.Domain.Experiments;

namespace Trading.ArchitectureTests;

public sealed class ExperimentWorkerTests
{
    [Fact]
    public void ExperimentWorkerRejectsInvalidStateTransitions()
    {
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Worker 1",
            "strategy-1",
            "BTCUSDT",
            10000m,
            DateTimeOffset.UtcNow,
            randomSeed: 1);

        worker.Start();
        worker.Pause();
        worker.Resume();

        Assert.Equal(ExperimentWorkerStatus.Running, worker.Status);

        worker.Complete();
        Assert.Equal(ExperimentWorkerStatus.Completed, worker.Status);

        var ex = Assert.Throws<InvalidOperationException>(() => worker.Start());
        Assert.Contains("cannot be started again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExperimentWorkerTracksPaperTradingLedgerAndLimits()
    {
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Worker 2",
            "strategy-2",
            "ETHUSDT",
            5000m,
            DateTimeOffset.UtcNow,
            randomSeed: 2);

        worker.Start();

        worker.ApplyPaperTrade(1m, 100m, 0.1m, "buy");
        worker.ApplyPaperTrade(0.5m, 110m, 0.05m, "sell");

        Assert.Equal(0.5m, worker.PositionQuantity);
        Assert.Equal(100.1m, worker.AverageEntryPrice);
        Assert.Equal(2, worker.Ledger.Count);
        Assert.True(worker.CashBalance < 5000m);

        // The opening fee is included in the decimal cost basis, then the sell fee is deducted.
        Assert.Equal(4.9m, worker.RealizedProfitAndLoss);

        var ex = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 100m, 0m, "sell"));
        Assert.Contains("Cannot sell more paper-trading quantity", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWorkerThatIsNotRunningCannotTrade()
    {
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Worker 3",
            "strategy-3",
            "ETHUSDT",
            5000m,
            DateTimeOffset.UtcNow,
            randomSeed: 3);

        var created = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 100m, 0m, "buy"));
        Assert.Contains("Only a running worker can trade", created.Message, StringComparison.Ordinal);

        worker.Start();
        worker.Fail("strategy template rejected the parameters");

        Assert.Equal("strategy template rejected the parameters", worker.FailureReason);

        var failed = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 100m, 0m, "buy"));
        Assert.Contains("Only a running worker can trade", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerNeverBorrowsCashToBuy()
    {
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Worker 4",
            "strategy-4",
            "ETHUSDT",
            100m,
            DateTimeOffset.UtcNow,
            randomSeed: 4);

        worker.Start();

        var ex = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(2m, 100m, 0m, "buy"));
        Assert.Contains("never borrow", ex.Message, StringComparison.Ordinal);
        Assert.Equal(100m, worker.CashBalance);
        Assert.Equal(0m, worker.PositionQuantity);
    }

    [Fact]
    public void AWorkerNeverAllowsFeesToMakeCashNegativeOnReduction()
    {
        var worker = CreateRunningWorker(100m);
        worker.ApplyPaperTrade(1m, 100m, 0m, "buy");

        var error = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 1m, 2m, "sell"));

        Assert.Contains("never borrow", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0m, worker.CashBalance);
        Assert.Equal(1m, worker.PositionQuantity);
        Assert.Single(worker.Ledger);
    }

    [Fact]
    public void WorkerLimitPreventsMoreThanTenWorkersPerUser()
    {
        Assert.True(ExperimentWorker.CanCreateMoreWorkers(9));
        Assert.False(ExperimentWorker.CanCreateMoreWorkers(10));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentWorker.CanCreateMoreWorkers(-1));
        Assert.Equal("existingWorkersCount", ex.ParamName);
    }

    [Fact]
    public void FavorableExperimentPaperAddUsesExactFeeInclusiveWeightedAverage()
    {
        var worker = CreateRunningWorker(1_000m);

        worker.ApplyPaperTrade(2m, 100m, 2m, "buy");
        worker.RecordFavorablePaperMark(110m);
        worker.ApplyPaperTrade(1m, 120m, 3m, "buy");

        Assert.Equal(3m, worker.PositionQuantity);
        Assert.Equal(108.33333333333333333333333333m, worker.AverageEntryPrice);
        Assert.Equal(1, worker.AdditionCount);
        Assert.Equal(3m, worker.TotalPurchasedQuantity);
        Assert.Equal(320m, worker.TotalPurchasedNotional);
        Assert.Null(worker.PriorFavorableMarkPrice);
        Assert.Equal(675m, worker.CashBalance);
    }

    [Fact]
    public void AddsRequirePriorFavorableMovementAndNeverAverageDown()
    {
        var worker = CreateRunningWorker(1_000m);
        worker.ApplyPaperTrade(1m, 100m, 0m, "buy");

        var noMark = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 110m, 0m, "buy"));
        Assert.Contains("prior realized favorable mark", noMark.Message, StringComparison.OrdinalIgnoreCase);

        worker.RecordFavorablePaperMark(110m);
        var adverse = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 99m, 0m, "buy"));
        Assert.Contains("averaging down", adverse.Message, StringComparison.OrdinalIgnoreCase);

        worker.ApplyPaperTrade(1m, 110m, 0m, "buy");
        Assert.Equal(2m, worker.PositionQuantity);
        Assert.Equal(105m, worker.AverageEntryPrice);
    }

    [Fact]
    public void AddFailureLeavesWorkerLedgerAndBalancesUnchanged()
    {
        var worker = CreateRunningWorker(1_000m);
        worker.ApplyPaperTrade(1m, 100m, 0m, "buy");
        var cash = worker.CashBalance;
        var quantity = worker.PositionQuantity;
        var average = worker.AverageEntryPrice;
        var ledgerCount = worker.Ledger.Count;

        Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 101m, 0m, "buy"));

        Assert.Equal(cash, worker.CashBalance);
        Assert.Equal(quantity, worker.PositionQuantity);
        Assert.Equal(average, worker.AverageEntryPrice);
        Assert.Equal(ledgerCount, worker.Ledger.Count);
    }

    [Fact]
    public void AdditionCountHasSmallPlatformBoundAndResetsAfterAFullReduction()
    {
        var controls = new ExperimentPaperPositionControls(1, 10m, 10_000m, 10m, 10_000m);
        var worker = CreateRunningWorker(10_000m, controls);
        worker.ApplyPaperTrade(1m, 100m, 0m, "buy");
        worker.RecordFavorablePaperMark(110m);
        worker.ApplyPaperTrade(1m, 110m, 0m, "buy");
        worker.RecordFavorablePaperMark(120m);

        var limit = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 120m, 0m, "buy"));
        Assert.Contains("maximum additions", limit.Message, StringComparison.OrdinalIgnoreCase);
        worker.ApplyPaperTrade(2m, 120m, 0m, "sell");
        Assert.Equal(0, worker.AdditionCount);

        var invalid = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExperimentPaperPositionControls(ExperimentPaperPositionControls.PlatformMaxAdditionsPerPosition + 1, 1m, 1m, 1m, 1m).Validate());
        Assert.Equal("MaxAdditionsPerPosition", invalid.ParamName);
    }

    [Theory]
    [InlineData(1, 10_000, 10, 10_000, "total purchased quantity")]
    [InlineData(10, 100, 10, 150, "total purchased notional")]
    [InlineData(10, 10_000, 1, 10_000, "position quantity")]
    [InlineData(10, 10_000, 10, 150, "position notional")]
    public void PaperWorkerHardLimitsRejectRatherThanResize(
        decimal totalQuantity, decimal totalNotional, decimal positionQuantity, decimal positionNotional, string reason)
    {
        var worker = CreateRunningWorker(10_000m,
            new ExperimentPaperPositionControls(1, totalQuantity, totalNotional, positionQuantity, positionNotional));

        var error = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(2m, 100m, 0m, "buy"));

        Assert.Contains(reason, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10_000m, worker.CashBalance);
        Assert.Equal(0m, worker.PositionQuantity);
        Assert.Empty(worker.Ledger);
    }

    [Fact]
    public void WorkerControlsAndPaperCostBasisAreIsolated()
    {
        var first = CreateRunningWorker(1_000m, new ExperimentPaperPositionControls(0, 1m, 100m, 1m, 100m));
        var second = CreateRunningWorker(1_000m);
        first.ApplyPaperTrade(1m, 100m, 0m, "buy");
        second.ApplyPaperTrade(1m, 100m, 0m, "buy");
        second.RecordFavorablePaperMark(110m);
        second.ApplyPaperTrade(1m, 110m, 0m, "buy");

        Assert.Equal(1m, first.PositionQuantity);
        Assert.Equal(0, first.AdditionCount);
        Assert.Equal(2m, second.PositionQuantity);
        Assert.Equal(1, second.AdditionCount);
    }

    private static ExperimentWorker CreateRunningWorker(decimal cash, ExperimentPaperPositionControls? controls = null)
    {
        var worker = new ExperimentWorker(Guid.NewGuid(), Guid.NewGuid(), "Paper add worker", "strategy", "BTCUSDT",
            cash, DateTimeOffset.UtcNow, 1, controls);
        worker.Start();
        return worker;
    }
}
