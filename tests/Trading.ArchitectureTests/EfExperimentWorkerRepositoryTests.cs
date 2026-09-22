using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.ArchitectureTests;

public sealed class EfExperimentWorkerRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReplaysImmutableOwnerScopedLedgerDeterministically()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var worker = new ExperimentWorker(Guid.NewGuid(), owner, "worker", "platform.ema-trend-continuation", "BTC/USD", 1_000m, Now, 7);
        worker.Start();
        worker.ApplyPaperTrade(2m, 100m, 1m, "buy", Now);
        var entry = worker.Ledger.Single();

        await using (var context = Context(database))
        {
            var repository = new EfExperimentWorkerRepository(context);
            await repository.SaveAsync(worker);
            await repository.AddAsync(owner, entry);
        }

        await using (var context = Context(database))
        {
            var repository = new EfExperimentWorkerRepository(context);
            var replayed = await repository.GetAsync(owner, worker.Id);

            Assert.NotNull(replayed);
            Assert.Equal(799m, replayed.CashBalance);
            Assert.Equal(2m, replayed.PositionQuantity);
            Assert.Equal(100.5m, replayed.AverageEntryPrice);
            Assert.Equal(worker.RandomSeed, replayed.RandomSeed);
            Assert.Equal(entry.Id, replayed.Ledger.Single().Id);
            var storedEntry = (await repository.ListAsync(owner, worker.Id)).Single();
            Assert.Equal(entry.Id, storedEntry.Id);
            Assert.Equal(entry.ExecutionPrice, storedEntry.ExecutionPrice);
            Assert.Equal(entry.Fee, storedEntry.Fee);
            Assert.Null(await repository.GetAsync(Guid.NewGuid(), worker.Id));
        }
    }

    [Fact]
    public async Task RejectsConflictingLedgerReplayIdentity()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var worker = new ExperimentWorker(Guid.NewGuid(), owner, "worker", "platform.ema-trend-continuation", "BTC/USD", 1_000m, Now, 7);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 0m, "buy", Now);
        var entry = worker.Ledger.Single();

        await using var context = Context(database);
        var repository = new EfExperimentWorkerRepository(context);
        await repository.SaveAsync(worker);
        await repository.AddAsync(owner, entry);
        var conflict = new PaperTradingLedgerEntry(entry.Id, entry.WorkerId, entry.Symbol, entry.Quantity, entry.ExecutionPrice,
            1m, entry.OccurredAtUtc, entry.Direction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(owner, conflict));
    }

    [Theory]
    [InlineData(ExperimentWorkerStatus.Failed)]
    [InlineData(ExperimentWorkerStatus.Completed)]
    public async Task ReplaysLedgerBeforeRestoringTerminalStatus(ExperimentWorkerStatus terminalStatus)
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            owner,
            "terminal worker",
            "platform.ema-trend-continuation",
            "BTC/USD",
            1_000m,
            Now,
            7);
        worker.Start();
        worker.ApplyPaperTrade(0.00132237m, 75_621.6m, 0.04999987m, "buy", Now);
        if (terminalStatus == ExperimentWorkerStatus.Failed)
            worker.Fail("Expected test failure.");
        else
            worker.Complete();

        await using (var context = Context(database))
        {
            var repository = new EfExperimentWorkerRepository(context);
            await repository.SaveAsync(worker);
            await repository.AddAsync(owner, worker.Ledger.Single());
        }

        await using (var context = Context(database))
        {
            var replayed = await new EfExperimentWorkerRepository(context).GetAsync(owner, worker.Id);

            Assert.NotNull(replayed);
            Assert.Equal(terminalStatus, replayed.Status);
            Assert.Equal(worker.CashBalance, replayed.CashBalance);
            Assert.Equal(worker.PositionQuantity, replayed.PositionQuantity);
            Assert.Equal(worker.AverageEntryPrice, replayed.AverageEntryPrice);
            Assert.Single(replayed.Ledger);
        }
    }

    [Fact]
    public async Task ApprovedPlanEvidenceIsOwnerScopedAndImmutable()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var key = new ExperimentDecisionKey(owner, Guid.NewGuid(), 1, ExperimentResearchGroup.A,
            "platform.ema-trend-continuation", 1, new string('A', 64), "BTC/USD", CandleInterval.OneHour,
            Now.AddHours(-1), Now, Now);
        var evidence = new ExperimentPaperPlanEvidence(key, 90m, 110m, Now);

        await using (var context = Context(database))
        {
            var repository = new EfExperimentPaperPlanEvidenceRepository(context);
            await repository.SaveAsync(evidence);
            await repository.SaveAsync(evidence);
        }

        await using (var context = Context(database))
        {
            var repository = new EfExperimentPaperPlanEvidenceRepository(context);
            var stored = Assert.Single(await repository.ListAsync(owner, key.WorkerId));
            Assert.Equal(90m, stored.ProtectiveStopPrice);
            Assert.Equal(110m, stored.ConservativeTargetPrice);
            Assert.Empty(await repository.ListAsync(Guid.NewGuid(), key.WorkerId));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.SaveAsync(evidence with { ProtectiveStopPrice = 89m }));
        }
    }

    [Fact]
    public async Task PaperTrainingActivationPersistsSelectedSlotsAndQualificationEvidence()
    {
        var database = Guid.NewGuid().ToString("N");
        var owner = Guid.NewGuid();
        var slot = PaperTrainingActivationService.ApprovedSlots[3] with
        {
            StartingCash = 2_500m,
            Interval = CandleInterval.FifteenMinutes
        };
        var result = new PaperTrainingQualificationResult(
            slot.Slot, slot.Symbol, false, -1.5m, 3, 4m, new string('C', 64),
            "Unqualified paper exploration only.",
            PaperOnlyExploration: true,
            Interval: slot.Interval);
        var activation = new PaperTrainingActivation(
            owner,
            PaperTrainingActivationState.Active,
            [slot],
            new(true, true, true, true, true, true),
            Now,
            owner,
            [result]);

        await using (var context = Context(database))
        {
            var repository = new EfPaperTrainingActivationRepository(context);
            Assert.True(await repository.TrySaveAsync(activation, null));
        }

        await using (var context = Context(database))
        {
            var stored = await new EfPaperTrainingActivationRepository(context).GetAsync(owner);

            Assert.NotNull(stored);
            Assert.Equal(slot, Assert.Single(stored.Slots));
            Assert.Equal(result, Assert.Single(stored.QualificationResults));
        }
    }

    private static TradingDbContext Context(string database) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(database).Options);
}
