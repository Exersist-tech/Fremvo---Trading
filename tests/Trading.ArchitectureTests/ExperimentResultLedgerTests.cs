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
    public void ResultUiUsesSafeTextRenderingAndHasNoControlsOrPromotionLanguage()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "wwwroot", "experiment-results.js"));
        var web = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));
        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("button", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("research-only", web, StringComparison.OrdinalIgnoreCase);
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
