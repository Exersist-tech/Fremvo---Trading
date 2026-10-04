using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.ArchitectureTests;

public sealed class PaperExecutionRecoveryTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("missing-fill", false)]
    [InlineData("missing-audit", false)]
    [InlineData("wrong-fee", false)]
    [InlineData("missing-portfolio", false)]
    [InlineData("unprotected-open-position", false)]
    public async Task RecoveryRequiresMatchingDurablePaperEvidenceAndAnOfflineHost(
        string scenario, bool shouldRecover)
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingPaperRecovery_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var owner = Guid.NewGuid();
        var administrator = Guid.NewGuid();
        var workerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        var opening = scenario == "unprotected-open-position";
        var correlation = $"paper-recovery-{Guid.NewGuid():N}";
        var key = new ExperimentDecisionKey(owner, workerId, 1, ExperimentResearchGroup.A,
            "platform.ema-trend-continuation", 1, new string('A', 64), "BTC/USD",
            CandleInterval.OneMinute, now.AddMinutes(-2), now.AddMinutes(-1), now);
        var claim = new ExperimentPaperExecutionAssociation(
            key, correlation, ExperimentPaperExecutionStatus.Claimed);

        try
        {
            await using (var db = Context())
            {
                Assert.True(await db.Database.EnsureCreatedAsync());
                var worker = new ExperimentWorker(
                    workerId, owner, "Recovery probe", key.StrategyId, key.Symbol,
                    1_000m, now.AddHours(-1), 7);
                worker.Start();
                var workers = new EfExperimentWorkerRepository(db);
                await workers.SaveAsync(worker);
                if (!opening)
                {
                    worker.ApplyPaperTrade(1m, 100m, 0.1m, "buy", now.AddMinutes(-10), Guid.NewGuid());
                    await workers.AddAsync(owner, worker.Ledger.Last());
                    await workers.SaveAsync(worker);
                }
                Assert.Equal(ExperimentPaperExecutionClaimResult.Claimed,
                    (await new EfExperimentPaperExecutionLedger(db).ClaimAsync(owner, claim)).Result);
                await new EfExperimentPaperExecutionLedger(db).CompleteAsync(owner,
                    claim with { Status = ExperimentPaperExecutionStatus.Unknown, ExecutionCommandId = commandId });
                worker.ApplyPaperTrade(1m, 100m, scenario == "wrong-fee" ? 0.2m : 0.1m,
                    opening ? "buy" : "sell", now, commandId);
                await workers.AddAsync(owner, worker.Ledger.Last());
                await workers.SaveAsync(worker);
                await new EfPaperHostHeartbeatRepository(db).RecordAsync(
                    EfPaperHostHeartbeatRepository.Experiments, now.AddMinutes(-5));
            }
            await using (var db = Context())
            {
                var context = new PipelineContext(owner, TradingMode.Paper, correlation);
                var direction = opening ? TradeDirection.Buy : TradeDirection.Sell;
                var command = new ExecutionCommand(commandId, Guid.NewGuid(), key.Symbol,
                    direction, 1m, 100m, now, $"paper-recovery-{commandId:N}");
                await new EfPaperExecutionCommands(db).AddAsync(
                    new PipelineRecord<ExecutionCommand>(Guid.NewGuid(), context,
                        PipelineStage.ExecutionCommand, command, now));
                if (scenario != "missing-fill")
                    await new EfPaperExecutionResults(db).AddAsync(
                        new PipelineRecord<PaperExecutionEvidence>(Guid.NewGuid(), context,
                            PipelineStage.Execution,
                            new PaperExecutionEvidence(commandId, ExecutionOutcome.Filled,
                                1m, 100m, 0.1m, now), now));
                if (scenario != "missing-portfolio")
                    await new EfPaperPortfolioUpdates(db).AddAsync(
                        new PipelineRecord<PortfolioUpdate>(Guid.NewGuid(), context,
                            PipelineStage.PortfolioUpdate,
                            new PortfolioUpdate(Guid.NewGuid(), commandId, key.Symbol,
                                opening ? 0m : 1m, opening ? 1m : 0m,
                                opening ? 1_000m : 899.9m, opening ? 899.9m : 999.8m,
                                0m, 0.1m, now), now));
                if (scenario != "missing-audit")
                {
                    db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), owner,
                        "Trade.PaperExecuted", "TradePipeline", commandId.ToString("D"),
                        now, null, "Filled simulated order.", correlation));
                    await db.SaveChangesAsync();
                }
            }

            await using (var db = Context())
            {
                var executions = new EfExperimentPaperExecutionLedger(db);
                Assert.Equal(PaperExecutionRecoveryResult.NoUnresolvedClaim,
                    await executions.ReconcileFilledAsync(Guid.NewGuid(), workerId, administrator,
                        correlation, now));
                Assert.Equal(PaperExecutionRecoveryResult.NoUnresolvedClaim,
                    await executions.ReconcileFilledAsync(owner, workerId, administrator,
                        "paper-wrong-correlation", now));
                Assert.True(await executions.HasUnresolvedAsync(owner, workerId));
                Assert.Equal(ExperimentPaperExecutionStatus.Unknown,
                    (await executions.GetUnresolvedAsync(owner, workerId))?.Status);
            }
            async Task<PaperExecutionRecoveryResult> AttemptRecoveryAsync()
            {
                await using var db = Context();
                var executions = new EfExperimentPaperExecutionLedger(db);
                return await executions.ReconcileFilledAsync(owner, workerId, administrator,
                    correlation, now);
            }
            if (shouldRecover)
            {
                var outcomes = await Task.WhenAll(AttemptRecoveryAsync(), AttemptRecoveryAsync());
                Assert.Single(outcomes, outcome => outcome == PaperExecutionRecoveryResult.Reconciled);
                Assert.Single(outcomes, outcome => outcome == PaperExecutionRecoveryResult.NoUnresolvedClaim);
            }
            else
                Assert.Equal(PaperExecutionRecoveryResult.EvidenceIncomplete, await AttemptRecoveryAsync());
            await using (var db = Context())
            {
                var executions = new EfExperimentPaperExecutionLedger(db);
                Assert.Equal(!shouldRecover, await executions.HasUnresolvedAsync(owner, workerId));
                Assert.Equal(!shouldRecover, await executions.GetUnresolvedAsync(owner, workerId) is not null);
                Assert.Equal((int)ExperimentPaperExecutionStatus.Unknown,
                    (await db.ExperimentPaperExecutionAssociations.AsNoTracking()
                        .SingleAsync()).Status);
                Assert.Equal(shouldRecover ? 1 : 0, await db.AuditEvents.CountAsync(
                    audit => audit.Action == "PaperExecution.FilledReconciled"
                        && audit.ActorUserId == administrator));
                if (shouldRecover)
                {
                    Assert.Equal(PaperExecutionRecoveryResult.NoUnresolvedClaim,
                        await executions.ReconcileFilledAsync(owner, workerId, administrator, correlation, now));
                    Assert.Equal(ExperimentPaperExecutionClaimResult.Existing,
                        (await executions.ClaimAsync(owner, claim)).Result);
                    var nextKey = key with
                    {
                        OpenTimeUtc = now.AddMinutes(-1),
                        CloseTimeUtc = now,
                        AsOfUtc = now.AddMinutes(1)
                    };
                    Assert.Equal(ExperimentPaperExecutionClaimResult.Claimed,
                        (await executions.ClaimAsync(owner,
                            new(nextKey, $"paper-next-{Guid.NewGuid():N}",
                                ExperimentPaperExecutionStatus.Claimed))).Result);
                }
            }
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task FreshExperimentHeartbeatCannotReleaseUnresolvedWorker()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingPaperRecoveryHeartbeat_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = new ExperimentDecisionKey(owner, worker, 1, ExperimentResearchGroup.A,
            "platform.ema-trend-continuation", 1, new string('B', 64), "BTC/USD",
            CandleInterval.OneMinute, now.AddMinutes(-2), now.AddMinutes(-1), now);
        var claim = new ExperimentPaperExecutionAssociation(key, "paper-host-running",
            ExperimentPaperExecutionStatus.Claimed);
        try
        {
            await using (var db = Context())
            {
                Assert.True(await db.Database.EnsureCreatedAsync());
                await new EfExperimentPaperExecutionLedger(db).ClaimAsync(owner, claim);
                await new EfPaperHostHeartbeatRepository(db).RecordAsync(
                    EfPaperHostHeartbeatRepository.Experiments, now);
            }
            await using (var db = Context())
            {
                var executions = new EfExperimentPaperExecutionLedger(db);
                Assert.Equal(PaperExecutionRecoveryResult.HostNotStopped,
                    await executions.ReconcileFilledAsync(owner, worker, Guid.NewGuid(),
                        claim.CorrelationId, now));
                Assert.True(await executions.HasUnresolvedAsync(owner, worker));
            }
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
