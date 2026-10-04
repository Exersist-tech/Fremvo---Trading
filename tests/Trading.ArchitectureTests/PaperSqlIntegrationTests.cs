using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Identity;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.ArchitectureTests;

public sealed class PaperSqlIntegrationTests
{
    [Fact]
    public async Task ActiveStrategyEditSurvivesSqlRestartAndRejectsStaleScannerSave()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingActiveStrategy_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var owner = Guid.NewGuid();
        var startedAt = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "BTC/USD",
            ProvenanceId = "scan-1234567890ABCDEF12345678"
        };
        var assignments = Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser)
            .Select(index => new PaperTrainingStrategyAssignment(index,
                PaperTrainingActivationService.ApprovedSlots[0].StrategyId,
                """{"regimeFastEma":40}"""))
            .ToArray();

        try
        {
            await using (var db = Context())
            {
                await db.Database.MigrateAsync();
                var original = new PaperTrainingActivation(owner, PaperTrainingActivationState.Active,
                    [slot], new(true, true, true, true, true, true), startedAt, owner);
                Assert.True(await new EfPaperTrainingActivationRepository(db).TrySaveAsync(original, null));
            }

            PaperTrainingActivation staleScanner;
            await using (var db = Context())
                staleScanner = (await new EfPaperTrainingActivationRepository(db).GetAsync(owner))!;

            await using (var db = Context())
            {
                var service = new PaperTrainingActivationService(
                    new EfPaperTrainingActivationRepository(db), new InMemoryAuditEventWriter(), TimeProvider.System);
                var saved = await service.ConfigureScannerStrategiesAsync(owner, owner, RoleType.User, assignments);
                Assert.Equal(startedAt, saved.ChangedAtUtc);
                Assert.Equal([slot], saved.Slots);
            }

            await using (var db = Context())
            {
                var repository = new EfPaperTrainingActivationRepository(db);
                var reloaded = (await repository.GetAsync(owner))!;
                Assert.True(reloaded.IsActive);
                Assert.Equal(startedAt, reloaded.ChangedAtUtc);
                Assert.Equal([slot], reloaded.Slots);
                Assert.All(reloaded.ConfiguredStrategies, assignment =>
                    Assert.Contains(@"""regimeFastEma"":40", assignment.StrategyParameters, StringComparison.Ordinal));
                Assert.False(await repository.TrySaveAsync(reloaded with
                {
                    PersistenceRevision = null,
                    Slots = []
                }, PaperTrainingActivationState.Active));
                Assert.False(await repository.TrySaveAsync(staleScanner with
                {
                    Slots = [],
                    Qualifications = []
                }, PaperTrainingActivationState.Active));
            }

            PaperTrainingActivation staleEditor;
            await using (var db = Context())
                staleEditor = (await new EfPaperTrainingActivationRepository(db).GetAsync(owner))!;
            await using (var db = Context())
            {
                var repository = new EfPaperTrainingActivationRepository(db);
                var scannerUpdate = (await repository.GetAsync(owner))! with
                {
                    Qualifications = [new PaperTrainingQualificationResult(
                        1, slot.Symbol, false, 0m, 0, 0m, "scan", "No qualifying signal")]
                };
                Assert.True(await repository.TrySaveAsync(scannerUpdate, PaperTrainingActivationState.Active));
            }
            await using (var db = Context())
            {
                var repository = new EfPaperTrainingActivationRepository(db);
                Assert.False(await repository.TrySaveAsync(staleEditor with
                {
                    StrategyAssignments = staleEditor.ConfiguredStrategies
                        .Select(item => item with { StrategyParameters = "{}" }).ToArray()
                }, PaperTrainingActivationState.Active));
            }

            await using (var db = Context())
            {
                var reloaded = (await new EfPaperTrainingActivationRepository(db).GetAsync(owner))!;
                Assert.Equal([slot], reloaded.Slots);
                Assert.Equal(10, reloaded.ConfiguredStrategies.Count);
                Assert.Single(reloaded.QualificationResults);
                Assert.All(reloaded.ConfiguredStrategies, assignment =>
                    Assert.Contains(@"""regimeFastEma"":40", assignment.StrategyParameters, StringComparison.Ordinal));
                Assert.Equal(startedAt, reloaded.ChangedAtUtc);
            }
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task EmergencyStoppedPaperOwnerKeepsCloseOnlySubscriptionsUntilFlatInSql()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingEmergencyProtection_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var owner = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with { Symbol = "BTC/USD" };
        try
        {
            await using (var db = Context())
            {
                Assert.True(await db.Database.EnsureCreatedAsync());
                var worker = new ExperimentWorker(Guid.NewGuid(), owner, "Paper opportunity emergency",
                    slot.StrategyId, slot.Symbol, slot.StartingCash, now, slot.Seed);
                worker.Start();
                worker.ApplyPaperTrade(1m, 100m, 0.1m, "buy", now);
                var workers = new EfExperimentWorkerRepository(db);
                await workers.SaveAsync(worker);
                await workers.AddAsync(owner, worker.Ledger.Last());
                Assert.True(await new EfPaperTrainingActivationRepository(db).TrySaveAsync(
                    new PaperTrainingActivation(owner, PaperTrainingActivationState.EmergencyStopped,
                        [slot], new(true, true, true, true, true, true), now, owner), null));
            }

            await using (var db = Context())
            {
                var activation = new EfPaperTrainingActivationRepository(db);
                Assert.DoesNotContain(owner, await activation.GetActiveOwnerIdsAsync());
                Assert.Contains(owner, await activation.GetProtectedOwnerIdsAsync());
                Assert.Contains(await activation.GetActiveSubscriptionsAsync(),
                    subscription => subscription.Symbol == slot.Symbol
                        && subscription.Interval == CandleInterval.OneMinute);
                var workers = new EfExperimentWorkerRepository(db);
                var worker = Assert.Single(await workers.ListAsync(owner));
                worker.ApplyPaperTrade(1m, 101m, 0.1m, "sell", now.AddMinutes(1));
                await workers.AddAsync(owner, worker.Ledger.Last());
                await workers.SaveAsync(worker);
            }

            await using (var db = Context())
            {
                var activation = new EfPaperTrainingActivationRepository(db);
                Assert.DoesNotContain(owner, await activation.GetProtectedOwnerIdsAsync());
                Assert.Empty(await activation.GetActiveSubscriptionsAsync());
            }
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task WorkerWaitsForWebSchemaBootstrapWithoutCreatingTablesItself()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var database = $"TradingSchemaReadiness_{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlServer(connection))
            .BuildServiceProvider();
        var readiness = new PaperHostSchemaReadiness(
            services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<PaperHostSchemaReadiness>.Instance, TimeSpan.FromSeconds(45));
        try
        {
            var awaitingSchema = readiness.StartAsync(CancellationToken.None);
            await Task.Delay(500);
            Assert.False(awaitingSchema.IsCompleted);

            await using (var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options))
                Assert.True(await db.Database.EnsureCreatedAsync());

            await awaitingSchema.WaitAsync(TimeSpan.FromSeconds(30));
            await readiness.StartAsync(CancellationToken.None);

            await using (var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options))
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE [ExperimentWorkers] DROP COLUMN [FailureReason]");
            var incomplete = new PaperHostSchemaReadiness(
                services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
                NullLogger<PaperHostSchemaReadiness>.Instance, TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                incomplete.StartAsync(CancellationToken.None));
            await using (var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options))
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE [ExperimentWorkers] ADD [FailureReason] nvarchar(512) NULL");
            await readiness.StartAsync(CancellationToken.None);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task DurablePaperClaimsAndSafetyEvidenceSurviveFreshSqlContexts()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var database = $"TradingValidation_{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);

        try
        {
            await using (var db = Context())
                Assert.True(await db.Database.EnsureCreatedAsync());

            var owner = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
            var pipeline = new PipelineContext(owner, TradingMode.Paper, "sql-integration");
            var marketEvent = new MarketEvent(
                Guid.NewGuid(), "BTC/USD", CandleInterval.OneMinute, now, 100m, 1m, true);
            var record = new PipelineRecord<MarketEvent>(
                Guid.NewGuid(), pipeline, PipelineStage.MarketEvent, marketEvent, now);

            await using (var db = Context())
            {
                await new EfPaperMarketEvents(db).AddAsync(record);
                await new EfPaperHostHeartbeatRepository(db).RecordAsync(
                    EfPaperHostHeartbeatRepository.Experiments, now);
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), owner,
                    "Trading.EmergencyStopEngaged", "TradingHalt", "platform", now,
                    null, "operator decision", "sql-halt"));
                await db.SaveChangesAsync();
            }
            await using (var db = Context())
            {
                var stored = await new EfPaperMarketEvents(db).GetAsync(owner, record.Id);
                Assert.NotNull(stored);
                Assert.Equal(100m, stored.Payload.LastPrice);
                Assert.Null(await new EfPaperMarketEvents(db).GetAsync(Guid.NewGuid(), record.Id));
                Assert.Equal(now, await new EfPaperHostHeartbeatRepository(db).LastAsync(
                    EfPaperHostHeartbeatRepository.Experiments));
                Assert.True((await new EfAuditTradingHaltState(db).GetAsync(
                    pipeline, marketEvent.Symbol, Guid.NewGuid(), CancellationToken.None)).EmergencyStop);
            }

            var key = new ExperimentDecisionKey(owner, Guid.NewGuid(), 1, ExperimentResearchGroup.A,
                "platform.ema-trend-continuation", 1, new string('A', 64), marketEvent.Symbol,
                CandleInterval.OneMinute, now.AddMinutes(-1), now, now);
            var paperWorker = new ExperimentWorker(
                key.WorkerId, owner, "Recovery probe", key.StrategyId, key.Symbol, 1_000m, now, 7);
            paperWorker.Start();
            var linkedCommandId = Guid.NewGuid();
            var paperBuy = new PaperTradingLedgerEntry(
                linkedCommandId, key.WorkerId, key.Symbol, 1m, 100m, 0.1m, now, "buy");
            await using (var db = Context())
            {
                var workers = new EfExperimentWorkerRepository(db);
                await workers.SaveAsync(paperWorker);
                await workers.AddAsync(owner, paperBuy);
            }
            await using (var db = Context())
            {
                var workers = new EfExperimentWorkerRepository(db);
                Assert.Null(await workers.GetAsync(Guid.NewGuid(), key.WorkerId));
                var replayed = await workers.GetAsync(owner, key.WorkerId);
                Assert.NotNull(replayed);
                Assert.Equal(1m, replayed.PositionQuantity);
                Assert.Equal(899.9m, replayed.CashBalance);
                Assert.Equal(paperBuy.Id, Assert.Single(replayed.Ledger).Id);
            }
            var claim = new ExperimentPaperExecutionAssociation(
                key, "paper-sql-integration-claim", ExperimentPaperExecutionStatus.Claimed);
            await using (var db = Context())
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    new EfExperimentPaperExecutionLedger(db).ClaimAsync(owner,
                        claim with { Status = ExperimentPaperExecutionStatus.Completed }));
            async Task<ExperimentPaperExecutionClaimResult> ClaimAsync()
            {
                await using var db = Context();
                return (await new EfExperimentPaperExecutionLedger(db).ClaimAsync(owner, claim)).Result;
            }
            var claims = await Task.WhenAll(ClaimAsync(), ClaimAsync());
            Assert.Single(claims, result => result == ExperimentPaperExecutionClaimResult.Claimed);
            Assert.Single(claims, result => result == ExperimentPaperExecutionClaimResult.Existing);
            var competingWorker = Guid.NewGuid();
            async Task<ExperimentPaperExecutionClaimResult> ClaimCompetingAsync(int minute)
            {
                var competingKey = key with
                {
                    WorkerId = competingWorker,
                    OpenTimeUtc = now.AddMinutes(minute - 1),
                    CloseTimeUtc = now.AddMinutes(minute),
                    AsOfUtc = now.AddMinutes(minute)
                };
                await using var db = Context();
                return (await new EfExperimentPaperExecutionLedger(db).ClaimAsync(owner,
                    new(competingKey, $"competing-{minute}", ExperimentPaperExecutionStatus.Claimed))).Result;
            }
            var competing = await Task.WhenAll(ClaimCompetingAsync(1), ClaimCompetingAsync(2));
            Assert.Single(competing, result => result == ExperimentPaperExecutionClaimResult.Claimed);
            Assert.Single(competing, result => result == ExperimentPaperExecutionClaimResult.Conflict);
            await using (var db = Context())
            {
                var executions = new EfExperimentPaperExecutionLedger(db);
                Assert.True(await executions.HasUnresolvedAsync(owner, key.WorkerId));
                Assert.Equal(claim, await executions.GetUnresolvedAsync(owner, key.WorkerId));
                Assert.False(await executions.HasUnresolvedAsync(Guid.NewGuid(), key.WorkerId));
                await executions.CompleteAsync(owner, claim with { Status = ExperimentPaperExecutionStatus.Unknown });
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executions.CompleteAsync(owner, claim with { Status = ExperimentPaperExecutionStatus.Blocked }));
            }
            var recordedCommand = new ExecutionCommand(linkedCommandId, Guid.NewGuid(), key.Symbol,
                TradeDirection.Buy, 1m, 100m, now, "paper-reconciliation-command");
            await using (var db = Context())
                await new EfPaperExecutionCommands(db).AddAsync(new PipelineRecord<ExecutionCommand>(
                    Guid.NewGuid(), new PipelineContext(owner, TradingMode.Paper, claim.CorrelationId),
                    PipelineStage.ExecutionCommand, recordedCommand, now));
            await using (var db = Context())
                await new EfPaperExecutionResults(db).AddAsync(new PipelineRecord<PaperExecutionEvidence>(
                    Guid.NewGuid(), new PipelineContext(owner, TradingMode.Paper, claim.CorrelationId),
                    PipelineStage.Execution,
                    new PaperExecutionEvidence(recordedCommand.Id, ExecutionOutcome.Filled, 1m, 100m, 0.1m, now), now));
            await using (var db = Context())
                await new EfPaperPortfolioUpdates(db).AddAsync(new PipelineRecord<PortfolioUpdate>(
                    Guid.NewGuid(), new PipelineContext(owner, TradingMode.Paper, claim.CorrelationId),
                    PipelineStage.PortfolioUpdate,
                    new PortfolioUpdate(Guid.NewGuid(), recordedCommand.Id, key.Symbol,
                        0m, 1m, 1_000m, 899.9m, 0m, 0.1m, now), now));
            await using (var db = Context())
            {
                db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), owner, "Trade.ExecutionUnknown",
                    "TradePipeline", recordedCommand.Id.ToString("D"), now, null,
                    "Reconciliation required.", claim.CorrelationId));
                await db.SaveChangesAsync();
            }
            await using (var db = Context())
            {
                var executions = new EfExperimentPaperExecutionLedger(db);
                Assert.True(await executions.HasUnresolvedAsync(owner, key.WorkerId));
                Assert.Equal(ExperimentPaperExecutionStatus.Unknown,
                    (await executions.GetUnresolvedAsync(owner, key.WorkerId))?.Status);
                Assert.Null(await executions.GetUnresolvedAsync(Guid.NewGuid(), key.WorkerId));
                var commandRecords = new EfPaperExecutionCommands(db);
                Assert.Equal(recordedCommand.Id, Assert.Single(await commandRecords.ListByCorrelationAsync(
                    owner, claim.CorrelationId)).Payload.Id);
                Assert.Equal(recordedCommand.Id,
                    Assert.Single((await new EfExperimentWorkerRepository(db).GetAsync(owner, key.WorkerId))!.Ledger).Id);
                Assert.Empty(await commandRecords.ListByCorrelationAsync(Guid.NewGuid(), claim.CorrelationId));
                var executionRecords = new EfPaperExecutionResults(db);
                var execution = Assert.Single(await executionRecords.ListByCorrelationAsync(owner, claim.CorrelationId));
                Assert.Equal(PipelineStage.Execution, execution.Stage);
                Assert.Equal(recordedCommand.Id, execution.Payload.ExecutionCommandId);
                Assert.Equal(ExecutionOutcome.Filled, execution.Payload.Outcome);
                Assert.Equal(0.1m, execution.Payload.Fees);
                Assert.Empty(await executionRecords.ListByCorrelationAsync(Guid.NewGuid(), claim.CorrelationId));
                var portfolioRecords = new EfPaperPortfolioUpdates(db);
                Assert.Equal(recordedCommand.Id, Assert.Single(await portfolioRecords.ListByCorrelationAsync(
                    owner, claim.CorrelationId)).Payload.ExecutionCommandId);
                Assert.Empty(await portfolioRecords.ListByCorrelationAsync(Guid.NewGuid(), claim.CorrelationId));
                var auditRecords = new EfPaperTradeAuditEvidenceReader(db);
                Assert.Equal(["Trade.ExecutionUnknown"], await auditRecords.ListActionsByCorrelationAsync(
                    owner, claim.CorrelationId));
                Assert.Empty(await auditRecords.ListActionsByCorrelationAsync(Guid.NewGuid(), claim.CorrelationId));
                var laterKey = key with
                {
                    OpenTimeUtc = now,
                    CloseTimeUtc = now.AddMinutes(1),
                    AsOfUtc = now.AddMinutes(1)
                };
                Assert.Equal(ExperimentPaperExecutionClaimResult.Conflict,
                    (await executions.ClaimAsync(owner,
                        new(laterKey, "later-candle", ExperimentPaperExecutionStatus.Claimed))).Result);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    executions.CompleteAsync(owner, claim with { Status = ExperimentPaperExecutionStatus.Blocked }));
            }
            await using (var db = Context())
            {
                Assert.True(await new EfExperimentPaperExecutionLedger(db).HasUnresolvedAsync(owner, key.WorkerId));
                Assert.Equal(1m, (await new EfExperimentWorkerRepository(db).GetAsync(owner, key.WorkerId))!.PositionQuantity);
            }
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
