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
        Assert.Equal(100m, worker.AverageEntryPrice);
        Assert.Equal(2, worker.Ledger.Count);
        Assert.True(worker.CashBalance < 5000m);

        // Sold 0.5 at 110 against a 100 average entry, less the 0.05 fee.
        Assert.Equal(4.95m, worker.RealizedProfitAndLoss);

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
    public void WorkerLimitPreventsMoreThanTenWorkersPerUser()
    {
        Assert.True(ExperimentWorker.CanCreateMoreWorkers(9));
        Assert.False(ExperimentWorker.CanCreateMoreWorkers(10));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ExperimentWorker.CanCreateMoreWorkers(-1));
        Assert.Equal("existingWorkersCount", ex.ParamName);
    }
}
