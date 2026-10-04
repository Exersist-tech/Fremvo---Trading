using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.ArchitectureTests;

public sealed class ExperimentResultLedgerTests
{
    [Fact]
    public async Task ResultSnapshotsAreOwnerScopedImmutableAndExplicitlyIdempotent()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var snapshot = Snapshot(owner, Guid.NewGuid());
        await using (var context = Context(database))
        {
            var ledger = new EfExperimentResultLedger(context);
            Assert.Equal(ExperimentResultWriteResult.Inserted, (await ledger.AppendAsync(owner, snapshot)).Result);
        }

        await using var restarted = Context(database);
        var ledgerAfterRestart = new EfExperimentResultLedger(restarted);
        Assert.Equal(ExperimentResultWriteResult.Duplicate, (await ledgerAfterRestart.AppendAsync(owner, snapshot)).Result);
        Assert.Equal(ExperimentResultWriteResult.Conflict,
            (await ledgerAfterRestart.AppendAsync(owner, snapshot with { Cash = 99m })).Result);
        Assert.Single((await ledgerAfterRestart.ListAsync(owner, 0, 25)).Items);
        Assert.Empty((await ledgerAfterRestart.ListAsync(Guid.NewGuid(), 0, 25)).Items);
    }

    [Fact]
    public void FactoryReconcilesDecimalLedgerAccountingAndDoesNotInventUnknownMetrics()
    {
        var owner = Guid.NewGuid();
        var worker = new ExperimentWorker(Guid.NewGuid(), owner, "isolated", "sma", "BTC/USD", 1000.12345678m, Now, 73);
        worker.Start();
        worker.ApplyPaperTrade(2.12345678m, 100m, 1.12345678m, "buy", Now);
        worker.ApplyPaperTrade(1m, 110m, 0.5m, "sell", Now.AddMinutes(1));

        var snapshot = ExperimentResultSnapshotFactory.Create(
            owner, "result-a", Provenance(worker), Now.AddMinutes(2), worker, null, null, null, null);

        Assert.Equal(worker.CashBalance, snapshot.Cash);
        Assert.Equal(worker.PositionQuantity, snapshot.PositionQuantity);
        Assert.Equal(worker.RealizedProfitAndLoss, snapshot.RealizedProfitAndLoss);
        Assert.Equal(1.62345678m, snapshot.Fees);
        Assert.Null(snapshot.Equity);
        Assert.Null(snapshot.UnrealizedProfitAndLoss);
        Assert.Null(snapshot.Exposure);
        Assert.Null(snapshot.Slippage);
        Assert.Null(snapshot.RejectedFillCount);
        Assert.Null(snapshot.MaximumDrawdown);
    }

    [Fact]
    public void FactoryCalculatesDrawdownFromSuppliedEquityEvidenceOnly()
    {
        var owner = Guid.NewGuid();
        var worker = new ExperimentWorker(Guid.NewGuid(), owner, "isolated", "sma", "BTC/USD", 1000m, Now, 73);
        var snapshot = ExperimentResultSnapshotFactory.Create(owner, "result-b", Provenance(worker), Now, worker, 100m,
            Array.Empty<ExperimentPaperFillRecord>(), 0, 0, EquityObservations);
        Assert.Equal(35m, snapshot.MaximumDrawdown);
        Assert.Equal(0m, snapshot.Slippage);
        Assert.Equal(0, snapshot.RejectedFillCount);
    }

    [Fact]
    public void ClosedTradeFactoryCalculatesFeeInclusiveRoundTripWithAddsAndPartialExits()
    {
        var owner = Guid.NewGuid();
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            owner,
            "closed-round-trip",
            "platform.ema-trend-continuation",
            "ETH/EUR",
            1_000m,
            Now,
            73);
        worker.Start();
        worker.ApplyPaperTrade(2m, 100m, 1m, "buy", Now);
        worker.RecordFavorablePaperMark(110m);
        worker.ApplyPaperTrade(1m, 105m, 0.5m, "buy", Now.AddMinutes(1));
        worker.ApplyPaperTrade(1m, 110m, 0.2m, "sell", Now.AddMinutes(2));
        worker.ApplyPaperTrade(2m, 90m, 0.4m, "sell", Now.AddMinutes(3));
        worker.Complete();

        var result = ExperimentClosedTradeResultFactory.Create(worker, "Maximum holding limit reached.");

        Assert.Equal(3m, result.Quantity);
        Assert.Equal(305m / 3m, result.AverageBuyFillPrice);
        Assert.Equal(306.5m / 3m, result.AverageEntryPrice);
        Assert.Equal(290m / 3m, result.AverageExitPrice);
        Assert.Equal(-15m, result.GrossProfitAndLoss);
        Assert.Equal(2.1m, result.Fees);
        Assert.Equal(-17.1m, result.NetProfitAndLoss);
        Assert.Equal(-17.1m / 306.5m * 100m, result.ReturnPercent);
        Assert.Equal(180, result.HoldingSeconds);
        Assert.Equal(2, result.BuyFillCount);
        Assert.Equal(2, result.SellFillCount);
        Assert.Equal(1, result.Additions);
        Assert.Equal(982.9m, result.EndingCash);
        Assert.Equal("Maximum holding limit reached.", result.ExitReason);
    }

    [Fact]
    public void FailedWorkerWithProtectiveCloseHasARealClosedTradeButOpenFailureDoesNot()
    {
        var worker = new ExperimentWorker(
            Guid.NewGuid(), Guid.NewGuid(), "failed-close", "strategy", "BTC/EUR", 1_000m, Now, 2);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 1m, "buy", Now);
        worker.Fail("Worker faulted after entry.");
        Assert.Throws<InvalidOperationException>(() => ExperimentClosedTradeResultFactory.Create(worker));
        worker.ApplyPaperTrade(1m, 110m, 1m, "sell", Now.AddMinutes(1));

        var result = ExperimentClosedTradeResultFactory.Create(worker);

        Assert.Equal(8m, result.NetProfitAndLoss);
        Assert.Equal(2m, result.Fees);
        Assert.Equal(1, result.BuyFillCount);
        Assert.Equal(1, result.SellFillCount);
        Assert.Equal(1_008m, result.EndingCash);
        Assert.Equal(ExperimentWorkerStatus.Failed, worker.Status);
    }

    [Fact]
    public async Task ClosedWorkerQueryIsOwnerScopedAndExcludesUnfilledAttempts()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        await using var context = Context(database);
        var repository = new EfExperimentWorkerRepository(context);
        var closed = ClosedWorker(owner, "closed");
        var unfilled = new ExperimentWorker(
            Guid.NewGuid(), owner, "unfilled", "strategy", "BTC/EUR", 1_000m, Now, 2);
        unfilled.Start();
        unfilled.Complete();
        var foreign = ClosedWorker(Guid.NewGuid(), "foreign");
        var failed = new ExperimentWorker(
            Guid.NewGuid(), owner, "failed", "strategy", "BTC/EUR", 1_000m, Now, 2);
        failed.Start();
        failed.ApplyPaperTrade(1m, 100m, 1m, "buy", Now);
        failed.Fail("Faulted after entry.");
        failed.ApplyPaperTrade(1m, 110m, 1m, "sell", Now.AddMinutes(1));
        await repository.SaveAsync(closed);
        foreach (var entry in closed.Ledger)
            await repository.AddAsync(owner, entry);
        await repository.SaveAsync(failed);
        foreach (var entry in failed.Ledger)
            await repository.AddAsync(owner, entry);
        await repository.SaveAsync(unfilled);
        await repository.SaveAsync(foreign);
        foreach (var entry in foreign.Ledger)
            await repository.AddAsync(foreign.UserId, entry);

        var results = await repository.ListClosedAsync(owner, 0, 25);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.Id == closed.Id);
        Assert.Contains(results, result => result.Id == failed.Id);
        Assert.All(results, result => Assert.True(
            ExperimentClosedTradeResultFactory.Create(result).SellFillCount > 0));
    }

    [Fact]
    public void ResultUiUsesSafeTextRenderingAndHasNoControlsOrPromotionLanguage()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "wwwroot", "experiment-results.js"));
        var web = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));
        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.Contains("closed-trades", script, StringComparison.Ordinal);
        Assert.Contains("Net P&L", script, StringComparison.Ordinal);
        Assert.Contains("/experiments.css", web, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("button", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("research-only", web, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("worker.OpenBuyFillPrice,", web, StringComparison.Ordinal);
        Assert.Contains("worker.LastBuyFillPrice,", web, StringComparison.Ordinal);
        Assert.Contains("worker.LastSellFillPrice,", web, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(\"/api/experiment-results", web, StringComparison.Ordinal);
        Assert.DoesNotContain("winner", script, StringComparison.OrdinalIgnoreCase);
    }

    private static TradingDbContext Context(string name) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name).Options);

    private static ExperimentResultSnapshot Snapshot(Guid owner, Guid worker) =>
        new(owner, "snapshot-001", new(worker, 1, "A", "sma", 1, "parameters", "dataset", "classifier-v1",
            "gate-evidence", 73, "reproducible-identity"), Now, 101m, 100m, 1m, 1m, 0m, 2m, 1m, 0.2m, 1, 2, 100m, 3);

    private static ExperimentResultProvenance Provenance(ExperimentWorker worker) =>
        new(worker.Id, 1, "A", "sma", 1, "parameters", "dataset", "classifier-v1", "gate-evidence", worker.RandomSeed, "reproducible-identity");

    private static ExperimentWorker ClosedWorker(Guid owner, string name)
    {
        var worker = new ExperimentWorker(
            Guid.NewGuid(), owner, name, "strategy", "BTC/EUR", 1_000m, Now, 2);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 1m, "buy", Now);
        worker.ApplyPaperTrade(1m, 110m, 1m, "sell", Now.AddMinutes(1));
        worker.Complete();
        return worker;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trading.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly decimal[] EquityObservations = [100m, 125m, 110m, 90m];
}
