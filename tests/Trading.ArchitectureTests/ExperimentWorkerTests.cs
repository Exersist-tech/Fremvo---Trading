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
            DateTimeOffset.UtcNow);

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
            DateTimeOffset.UtcNow);

        worker.ApplyPaperTrade(1m, 100m, 0.1m, "buy");
        worker.ApplyPaperTrade(0.5m, 110m, 0.05m, "sell");

        Assert.Equal(0.5m, worker.PositionQuantity);
        Assert.Equal(100m, worker.AverageEntryPrice);
        Assert.Equal(2, worker.Ledger.Count);
        Assert.True(worker.CashBalance < 5000m);

        var ex = Assert.Throws<InvalidOperationException>(() => worker.ApplyPaperTrade(1m, 100m, 0m, "sell"));
        Assert.Contains("Cannot sell more paper-trading quantity", ex.Message, StringComparison.OrdinalIgnoreCase);
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
