using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.ArchitectureTests;

public sealed class ExperimentDecisionLedgerTests
{
    [Fact]
    public async Task DurableLedgerIsOwnerScopedAndReturnsDuplicateOrConflictAfterRestart()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var record = Record(owner, worker, ExperimentProposalAction.Open, "evidence-a");
        await using (var context = Context(database))
        {
            var ledger = new EfExperimentDecisionLedger(context);
            Assert.Equal(ExperimentDecisionWriteResult.Inserted, (await ledger.RecordAsync(owner, record)).Result);
        }

        await using var restartedContext = Context(database);
        var restarted = new EfExperimentDecisionLedger(restartedContext);
        Assert.Equal(ExperimentDecisionWriteResult.Duplicate, (await restarted.RecordAsync(owner, record)).Result);
        Assert.Equal(ExperimentDecisionWriteResult.Conflict,
            (await restarted.RecordAsync(owner, record with { EvidenceFingerprint = "different-evidence" })).Result);
        Assert.Single(await restarted.ListAsync(owner, worker));
        Assert.Empty(await restarted.ListAsync(Guid.NewGuid(), worker));
    }

    [Fact]
    public async Task LedgerHonorsCancellationWithoutWriting()
    {
        await using var context = Context(Guid.NewGuid().ToString("N"));
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new EfExperimentDecisionLedger(context).RecordAsync(owner, Record(owner, worker, ExperimentProposalAction.Neutral, "evidence"), cancelled.Token));
        Assert.Empty(await new EfExperimentDecisionLedger(context).ListAsync(owner, worker));
    }

    private static TradingDbContext Context(string name) => new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name).Options);
    private static ExperimentDecisionRecord Record(Guid owner, Guid worker, ExperimentProposalAction action, string evidence) =>
        new(new ExperimentDecisionKey(owner, worker, 1, ExperimentResearchGroup.A, "experiment-sma-trend", 1,
            new string('A', 64), "BTC/USD", CandleInterval.OneHour, Now.AddHours(-1), Now, Now),
            new ExperimentProposal(action, "recorded reason"), evidence, Now);
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
}
