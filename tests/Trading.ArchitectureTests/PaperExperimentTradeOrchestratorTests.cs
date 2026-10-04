using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class PaperExperimentTradeOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcceptedOpenUsesOnlyMandatoryPaperPipeline()
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open);
        var result = await orchestrator.ProcessAsync(proposal, context);

        Assert.True(result.Submitted);
        Assert.True(result.PipelineResult!.Executed);
        Assert.Equal(PipelineStage.AuditEvent, result.PipelineResult.ReachedStage);
        Assert.Single(adapter.Ledger);
        Assert.Single(await harness.Events.ListForUserAsync(proposal.Key.UserId));
        Assert.Single(await harness.Decisions.ListForUserAsync(proposal.Key.UserId));
        Assert.Single(await harness.Intents.ListForUserAsync(proposal.Key.UserId));
        Assert.Single(await harness.Risks.ListForUserAsync(proposal.Key.UserId));
        Assert.Single(await harness.Commands.ListForUserAsync(proposal.Key.UserId));
        var recordedExecution = Assert.Single(await harness.PaperResults.ListForUserAsync(proposal.Key.UserId));
        Assert.Equal(PipelineStage.Execution, recordedExecution.Stage);
        Assert.Equal(result.PipelineResult.ExecutionCommandId, recordedExecution.Payload.ExecutionCommandId);
        Assert.Equal(ExecutionOutcome.Filled, recordedExecution.Payload.Outcome);
        Assert.Equal(adapter.EstimatedTakerFeeRate * 100m, recordedExecution.Payload.Fees);
        Assert.Empty(await harness.PaperResults.ListForUserAsync(Guid.NewGuid()));
        Assert.Single(await harness.Portfolios.ListForUserAsync(proposal.Key.UserId));
        var update = (await harness.Portfolios.ListForUserAsync(proposal.Key.UserId)).Single().Payload;
        Assert.Equal(context.Worker.StartingCash, update.CashBalanceBefore);
        Assert.True(update.CashBalanceAfter < update.CashBalanceBefore);
        Assert.Contains(harness.Audit.Events, e => e.Action == "Trade.PaperExecuted");
        Assert.All(await harness.Commands.ListForUserAsync(proposal.Key.UserId), command => Assert.True(command.Payload.IsPaperOnly));
    }

    [Fact]
    public async Task AcceptedAddUsesExactApprovedQuantityOnPaperPipeline()
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Add, position: 1m, sized: true);

        var result = await orchestrator.ProcessSizedAsync(proposal, context, 0.25m);

        Assert.True(result.PipelineResult!.Executed);
        var intent = (await harness.Intents.ListForUserAsync(proposal.Key.UserId)).Single().Payload;
        Assert.Equal(TradeDirection.Buy, intent.Direction);
        Assert.Equal(0.25m, intent.Quantity);
        Assert.False(intent.ReduceOnly);
        Assert.False(intent.CloseOnly);
        Assert.Single(adapter.Ledger);
    }

    [Fact]
    public async Task SizedEntryKeepsSignalIdentityButValuesPipelineAndFillAtLaterPrice()
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open, sized: true);
        var repriced = context with
        {
            ExecutionCandle = context.ExecutionCandle! with { ClosePrice = 101m }
        };

        var result = await orchestrator.ProcessSizedAsync(proposal, repriced, 0.25m);

        Assert.True(result.PipelineResult!.Executed);
        Assert.Equal(101m, Assert.Single(adapter.Ledger).Price);
        var market = Assert.Single(await harness.Events.ListForUserAsync(proposal.Key.UserId)).Payload;
        Assert.Equal(101m, market.LastPrice);
        Assert.Equal(Now.AddMinutes(1), market.EventTimeUtc);
        Assert.Equal(101m, Assert.Single(await harness.Intents.ListForUserAsync(proposal.Key.UserId)).Payload.LimitPrice);
        Assert.Equal(25.25m, Assert.Single(await harness.Risks.ListForUserAsync(proposal.Key.UserId)).Payload.ProposedExposure);
        Assert.Equal(Now.AddMinutes(1), repriced.Portfolio.AsOfUtc);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("same-signal")]
    [InlineData("gap")]
    [InlineData("unsafe")]
    public async Task SizedEntryRejectsUnattestedSuccessorBeforeClaim(string problem)
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open, sized: true);
        var invalid = problem switch
        {
            "missing" => context with { ExecutionCandle = null },
            "same-signal" => context with
            {
                ExecutionCandle = context.ExecutionCandle! with { OpenTimeUtc = Now.AddMinutes(-1) }
            },
            "gap" => context with
            {
                ExecutionCandle = context.ExecutionCandle! with { CloseTimeUtc = Now.AddMinutes(2) },
                Portfolio = context.Portfolio with { AsOfUtc = Now.AddMinutes(2) }
            },
            "unsafe" => context with
            {
                ExecutionCandle = context.ExecutionCandle! with { QualityFlags = ["Stale"] }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ProcessSizedAsync(proposal, invalid, 0.25m));

        Assert.Empty(adapter.Ledger);
        Assert.Empty(await harness.Events.ListForUserAsync(proposal.Key.UserId));
        Assert.False(await harness.Executions.HasUnresolvedAsync(proposal.Key.UserId, proposal.Key.WorkerId));
    }

    [Fact]
    public async Task ExistingPositionNotionalAndActualWorkerCashLimitAnAdd()
    {
        var harness = new Harness(maxNotional: 100m);
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Add, position: 1m, sized: true);

        var result = await orchestrator.ProcessSizedAsync(proposal, context, 0.25m);

        Assert.False(result.PipelineResult!.Executed);
        Assert.Equal(PipelineStage.RiskEvaluation, result.PipelineResult.ReachedStage);
        Assert.Empty(adapter.Ledger);
        Assert.Equal(910m, context.Worker.CashBalance);
    }

    [Fact]
    public async Task AnUnaffordableBuyNeverReachesThePaperAdapter()
    {
        var harness = new Harness(startingCash: 50m);
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open, sized: true);

        var result = await orchestrator.ProcessSizedAsync(proposal, context, 1m);

        Assert.False(result.PipelineResult!.Executed);
        Assert.Equal(PipelineStage.RiskEvaluation, result.PipelineResult.ReachedStage);
        Assert.Empty(adapter.Ledger);
    }

    [Fact]
    public async Task AConfirmedFillWithoutEnoughCashForFeesRequiresReconciliation()
    {
        var harness = new Harness(startingCash: 100m);
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open, sized: true);

        var result = await orchestrator.ProcessSizedAsync(proposal, context, 1m);

        Assert.False(result.PipelineResult!.Executed);
        Assert.True(result.PipelineResult.RequiresReconciliation);
        Assert.Single(adapter.Ledger);
        Assert.Equal(ExecutionOutcome.Filled,
            Assert.Single(await harness.PaperResults.ListForUserAsync(proposal.Key.UserId)).Payload.Outcome);
        Assert.Empty(await harness.Portfolios.ListForUserAsync(proposal.Key.UserId));
        Assert.Contains(harness.Audit.Events, item => item.Action == "Trade.PortfolioReconciliationRequired");
        Assert.Equal(ExperimentPaperExecutionStatus.Unknown,
            (await harness.Executions.ClaimAsync(proposal.Key.UserId,
                new ExperimentPaperExecutionAssociation(proposal.Key, "ignored", ExperimentPaperExecutionStatus.Claimed)))
                .Association!.Status);
    }

    [Fact]
    public async Task FailedDurableExecutionEvidenceFreezesClaimWithoutResubmittingFill()
    {
        var harness = new Harness(paperResults: new RejectingPaperResults());
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ProcessAsync(proposal, context));

        Assert.Single(adapter.Ledger);
        Assert.Empty(await harness.Portfolios.ListForUserAsync(proposal.Key.UserId));
        Assert.True(await harness.Executions.HasUnresolvedAsync(proposal.Key.UserId, proposal.Key.WorkerId));
        Assert.False((await orchestrator.ProcessAsync(proposal, context)).Submitted);
        Assert.Single(adapter.Ledger);
    }

    [Fact]
    public async Task APositionSnapshotThatDoesNotMatchTheWorkerIsRejectedBeforeClaim()
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Add, position: 1m, sized: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ProcessSizedAsync(
            proposal, context with
            {
                Portfolio = context.Portfolio with { PositionQuantity = 0m }
            }, 0.25m));

        Assert.Empty(adapter.Ledger);
        Assert.Empty(await harness.Events.ListForUserAsync(proposal.Key.UserId));
    }

    [Theory]
    [InlineData(ExperimentProposalAction.Reduce, 3, 1, false)]
    [InlineData(ExperimentProposalAction.Close, 3, 3, true)]
    public async Task ReductionAndCloseMapToReduceOnlySell(
        ExperimentProposalAction action, decimal position, decimal expectedQuantity, bool closeOnly)
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(action, position);
        var result = await orchestrator.ProcessAsync(proposal, context);

        Assert.True(result.PipelineResult!.Executed);
        var intent = (await harness.Intents.ListForUserAsync(proposal.Key.UserId)).Single().Payload;
        Assert.Equal(TradeDirection.Sell, intent.Direction);
        Assert.Equal(expectedQuantity, intent.Quantity);
        Assert.True(intent.ReduceOnly);
        Assert.Equal(closeOnly, intent.CloseOnly);
        Assert.Single(adapter.Ledger);
    }

    [Fact]
    public async Task NeutralAndRiskDeniedProposalsNeverReachPaperAdapter()
    {
        var neutralHarness = new Harness();
        var (neutralOrchestrator, neutral, neutralContext, neutralAdapter) = neutralHarness.Create(ExperimentProposalAction.Neutral);
        var neutralResult = await neutralOrchestrator.ProcessAsync(neutral, neutralContext);
        Assert.False(neutralResult.Submitted);
        Assert.Empty(neutralAdapter.Ledger);
        Assert.Empty(await neutralHarness.Commands.ListForUserAsync(neutral.Key.UserId));

        var deniedHarness = new Harness(maxNotional: 1m);
        var (deniedOrchestrator, denied, deniedContext, deniedAdapter) = deniedHarness.Create(ExperimentProposalAction.Open);
        var deniedResult = await deniedOrchestrator.ProcessAsync(denied, deniedContext);
        Assert.True(deniedResult.Submitted);
        Assert.False(deniedResult.PipelineResult!.Executed);
        Assert.Equal(PipelineStage.RiskEvaluation, deniedResult.PipelineResult.ReachedStage);
        Assert.Empty(deniedAdapter.Ledger);
        Assert.Empty(await deniedHarness.Commands.ListForUserAsync(denied.Key.UserId));
    }

    [Fact]
    public async Task InvalidActionNeverClaimsOrExecutes()
    {
        var harness = new Harness();
        var (orchestrator, invalid, context, adapter) = harness.Create((ExperimentProposalAction)99);

        var result = await orchestrator.ProcessAsync(invalid, context);

        Assert.False(result.Submitted);
        Assert.Empty(adapter.Ledger);
        Assert.Empty(await harness.Commands.ListForUserAsync(invalid.Key.UserId));
    }

    [Fact]
    public async Task DuplicateAndUnknownClaimsAreNeverReexecuted()
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open);
        await orchestrator.ProcessAsync(proposal, context);
        var replay = await orchestrator.ProcessAsync(proposal, context);
        Assert.False(replay.Submitted);
        Assert.Single(adapter.Ledger);

        var unknownHarness = new Harness();
        var (unknownOrchestrator, unknown, unknownContext, unknownAdapter) = unknownHarness.Create(ExperimentProposalAction.Open);
        const string correlation = "already-unknown";
        await unknownHarness.Executions.ClaimAsync(unknown.Key.UserId,
            new ExperimentPaperExecutionAssociation(unknown.Key, correlation, ExperimentPaperExecutionStatus.Claimed));
        await unknownHarness.Executions.CompleteAsync(unknown.Key.UserId,
            new ExperimentPaperExecutionAssociation(unknown.Key, correlation, ExperimentPaperExecutionStatus.Unknown));
        var unresolved = await unknownHarness.Executions.GetUnresolvedAsync(unknown.Key.UserId, unknown.Key.WorkerId);
        Assert.Equal(ExperimentPaperExecutionStatus.Unknown, unresolved?.Status);
        Assert.Equal(correlation, unresolved?.CorrelationId);
        Assert.Null(await unknownHarness.Executions.GetUnresolvedAsync(Guid.NewGuid(), unknown.Key.WorkerId));
        var retry = await unknownOrchestrator.ProcessAsync(unknown, unknownContext);
        Assert.False(retry.Submitted);
        Assert.Empty(unknownAdapter.Ledger);
    }

    [Theory]
    [InlineData(ExperimentPaperExecutionStatus.Claimed)]
    [InlineData(ExperimentPaperExecutionStatus.Unknown)]
    public async Task DifferentCandleCannotIncreaseExposureWhileWorkerClaimIsUnresolved(
        ExperimentPaperExecutionStatus status)
    {
        var harness = new Harness();
        var (orchestrator, prior, context, adapter) = harness.Create(ExperimentProposalAction.Open, sized: true);
        var association = new ExperimentPaperExecutionAssociation(prior.Key, "pending", ExperimentPaperExecutionStatus.Claimed);
        await harness.Executions.ClaimAsync(prior.Key.UserId, association);
        if (status == ExperimentPaperExecutionStatus.Unknown)
            await harness.Executions.CompleteAsync(prior.Key.UserId, association with { Status = status });

        var nextKey = prior.Key with
        {
            OpenTimeUtc = prior.Key.OpenTimeUtc.AddHours(1),
            CloseTimeUtc = prior.Key.CloseTimeUtc.AddHours(1),
            AsOfUtc = prior.Key.AsOfUtc.AddHours(1)
        };
        var next = prior with { Key = nextKey, RecordedAtUtc = nextKey.AsOfUtc };
        await harness.DecisionsLedger.RecordAsync(nextKey.UserId, next);
        var nextContext = context with
        {
            Candle = context.Candle with
            {
                OpenTimeUtc = nextKey.OpenTimeUtc,
                CloseTimeUtc = nextKey.CloseTimeUtc,
                AsOfUtc = nextKey.AsOfUtc
            },
            Portfolio = context.Portfolio with { AsOfUtc = nextKey.AsOfUtc.AddMinutes(1) },
            ExecutionCandle = context.ExecutionCandle! with
            {
                OpenTimeUtc = nextKey.CloseTimeUtc,
                CloseTimeUtc = nextKey.CloseTimeUtc.AddMinutes(1),
                AsOfUtc = nextKey.CloseTimeUtc.AddMinutes(1)
            }
        };

        var result = await orchestrator.ProcessSizedAsync(next, nextContext, 0.5m);

        Assert.False(result.Submitted);
        Assert.Contains("reconciliation", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(adapter.Ledger);
        Assert.True(await harness.Executions.HasUnresolvedAsync(nextKey.UserId, nextKey.WorkerId));
        Assert.False(await harness.Executions.HasUnresolvedAsync(Guid.NewGuid(), nextKey.WorkerId));
    }

    [Theory]
    [InlineData(ExperimentPaperExecutionStatus.Claimed, ExperimentProposalAction.Reduce)]
    [InlineData(ExperimentPaperExecutionStatus.Unknown, ExperimentProposalAction.Reduce)]
    [InlineData(ExperimentPaperExecutionStatus.Claimed, ExperimentProposalAction.Close)]
    [InlineData(ExperimentPaperExecutionStatus.Unknown, ExperimentProposalAction.Close)]
    public async Task UnresolvedWorkerClaimBlocksAnotherReduction(
        ExperimentPaperExecutionStatus status, ExperimentProposalAction action)
    {
        var harness = new Harness();
        var (orchestrator, previous, context, adapter) = harness.Create(action, position: 3m);
        var pending = new ExperimentPaperExecutionAssociation(previous.Key, "pending", ExperimentPaperExecutionStatus.Claimed);
        await harness.Executions.ClaimAsync(previous.Key.UserId, pending);
        if (status == ExperimentPaperExecutionStatus.Unknown)
            await harness.Executions.CompleteAsync(previous.Key.UserId, pending with { Status = status });

        var nextKey = previous.Key with
        {
            OpenTimeUtc = previous.Key.OpenTimeUtc.AddHours(1),
            CloseTimeUtc = previous.Key.CloseTimeUtc.AddHours(1),
            AsOfUtc = previous.Key.AsOfUtc.AddHours(1)
        };
        var next = previous with { Key = nextKey, RecordedAtUtc = nextKey.AsOfUtc };
        await harness.DecisionsLedger.RecordAsync(nextKey.UserId, next);
        var nextContext = context with
        {
            Candle = context.Candle with
            {
                OpenTimeUtc = nextKey.OpenTimeUtc,
                CloseTimeUtc = nextKey.CloseTimeUtc,
                AsOfUtc = nextKey.AsOfUtc
            },
            Portfolio = context.Portfolio with { AsOfUtc = nextKey.AsOfUtc }
        };

        var result = await orchestrator.ProcessAsync(next, nextContext);

        Assert.False(result.Submitted);
        Assert.Contains("reconciliation", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(adapter.Ledger);
        Assert.Empty(await harness.Commands.ListForUserAsync(nextKey.UserId));
    }

    [Fact]
    public async Task TerminalPaperClaimCannotBeRewrittenOrCreatedWithoutAClaim()
    {
        var ledger = new InMemoryExperimentPaperExecutionLedger();
        var owner = Guid.NewGuid();
        var key = new ExperimentDecisionKey(owner, Guid.NewGuid(), 1, ExperimentResearchGroup.A,
            "platform.ema-trend-continuation", 1, new string('A', 64), "BTC/USD",
            CandleInterval.OneMinute, Now.AddMinutes(-1), Now, Now);
        var claim = new ExperimentPaperExecutionAssociation(key, "pending", ExperimentPaperExecutionStatus.Claimed);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ledger.ClaimAsync(owner, claim with { Status = ExperimentPaperExecutionStatus.Completed }));
        Assert.Equal(ExperimentPaperExecutionClaimResult.Claimed, (await ledger.ClaimAsync(owner, claim)).Result);
        await ledger.CompleteAsync(owner, claim with { Status = ExperimentPaperExecutionStatus.Unknown });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.CompleteAsync(owner, claim with { Status = ExperimentPaperExecutionStatus.Blocked }));
        Assert.True(await ledger.HasUnresolvedAsync(owner, key.WorkerId));
        var existing = await ledger.ClaimAsync(owner, claim);
        Assert.Equal(ExperimentPaperExecutionStatus.Unknown, existing.Association?.Status);
    }

    [Fact]
    public async Task CompetingWorkerClaimsAreAtomicAcrossDifferentDecisionKeys()
    {
        var ledger = new InMemoryExperimentPaperExecutionLedger();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var key = new ExperimentDecisionKey(owner, worker, 1, ExperimentResearchGroup.A,
            "platform.ema-trend-continuation", 1, new string('A', 64), "BTC/USD",
            CandleInterval.OneMinute, Now.AddMinutes(-1), Now, Now);

        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(index =>
            Task.Run(async () => (await ledger.ClaimAsync(owner,
                new(key with
                {
                    OpenTimeUtc = Now.AddMinutes(index - 1),
                    CloseTimeUtc = Now.AddMinutes(index),
                    AsOfUtc = Now.AddMinutes(index)
                }, $"competing-{index}", ExperimentPaperExecutionStatus.Claimed))).Result)));

        Assert.Single(results, result => result == ExperimentPaperExecutionClaimResult.Claimed);
        Assert.Equal(19, results.Count(result => result == ExperimentPaperExecutionClaimResult.Conflict));
    }

    [Fact]
    public async Task ForeignWorkerOrUnrecordedProposalIsRejectedBeforeExecution()
    {
        var harness = new Harness();
        var (orchestrator, proposal, context, adapter) = harness.Create(ExperimentProposalAction.Open);
        var foreignWorker = new ExperimentWorker(Guid.NewGuid(), proposal.Key.UserId, "foreign", proposal.Key.StrategyId, proposal.Key.Symbol, 1m, Now, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.ProcessAsync(proposal, context with { Worker = foreignWorker }));
        Assert.Empty(adapter.Ledger);

        var result = await orchestrator.ProcessAsync(proposal with { EvidenceFingerprint = "different" }, context);
        Assert.False(result.Submitted);
        Assert.Empty(adapter.Ledger);
    }

    private sealed class RecordingAudit : IAuditEventWriter
    {
        public List<AuditEvent> Events { get; } = new();
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public InMemoryMarketEventRepository Events { get; } = new();
        public InMemoryStrategyDecisionRepository Decisions { get; } = new();
        public InMemoryTradeIntentRepository Intents { get; } = new();
        public InMemoryRiskEvaluationRepository Risks { get; } = new();
        public InMemoryExecutionCommandRepository Commands { get; } = new();
        public InMemoryPaperExecutionResultRepository PaperResults { get; } = new();
        public InMemoryPortfolioUpdateRepository Portfolios { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public InMemoryExperimentDecisionLedger DecisionsLedger { get; } = new();
        public InMemoryExperimentPaperExecutionLedger Executions { get; } = new();
        private readonly decimal _maxNotional;
        private readonly decimal _startingCash;
        private readonly IPaperExecutionResultRepository? _paperResults;
        public Harness(decimal maxNotional = 1_000m, decimal startingCash = 1_000m,
            IPaperExecutionResultRepository? paperResults = null)
        {
            (_maxNotional, _startingCash) = (maxNotional, startingCash);
            _paperResults = paperResults;
        }

        public (PaperExperimentTradeOrchestrator, ExperimentDecisionRecord, ExperimentPaperWorkerContext, PaperExecutionAdapter) Create(
            ExperimentProposalAction action, decimal position = 0m, bool sized = false)
        {
            var user = Guid.NewGuid();
            var worker = new ExperimentWorker(Guid.NewGuid(), user, "paper-worker", "experiment-sma-trend", "BTC/USD", _startingCash, Now, 1);
            worker.Start();
            if (position > 0m)
                worker.ApplyPaperTrade(position, 90m, 0m, "buy", Now.AddHours(-1));
            var key = new ExperimentDecisionKey(user, worker.Id, 1, ExperimentResearchGroup.A, worker.StrategyId, 1,
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", worker.MarketSymbol, CandleInterval.OneHour,
                Now.AddHours(-1), Now, Now);
            var proposal = new ExperimentDecisionRecord(key, new ExperimentProposal(action, "approved test proposal"), "evidence", Now);
            DecisionsLedger.RecordAsync(user, proposal).GetAwaiter().GetResult();
            var pipeline = new TradePipeline(Events, Decisions, Intents, Risks, Commands, Portfolios, Audit,
                new RiskEngine(), new InMemoryTradingHaltState(), new OrderIdempotencyGuard(),
                new TradePipelineOptions { MaxNotional = _maxNotional, MaxPositionSize = 100m },
                timeProvider: new FixedTimeProvider(Now.AddMinutes(1).AddSeconds(5)),
                paperResults: _paperResults ?? PaperResults);
            var adapter = new PaperExecutionAdapter();
            var orchestrator = new PaperExperimentTradeOrchestrator(DecisionsLedger, Executions, pipeline, adapter);
            var candle = new ExperimentPaperCandleSnapshot(worker.MarketSymbol, CandleInterval.OneHour, key.OpenTimeUtc, key.CloseTimeUtc,
                key.AsOfUtc, 100m, 1m);
            var execution = new ExperimentPaperCandleSnapshot(worker.MarketSymbol, CandleInterval.OneMinute,
                key.CloseTimeUtc, key.CloseTimeUtc.AddMinutes(1), key.CloseTimeUtc.AddMinutes(1), 100m, 1m);
            return (orchestrator, proposal, new ExperimentPaperWorkerContext(worker,
                new ExperimentWorkerPortfolioSnapshot(user, worker.Id, position,
                    sized ? execution.CloseTimeUtc : Now), candle, execution), adapter);
        }
    }

    private sealed class RejectingPaperResults : IPaperExecutionResultRepository
    {
        public Task AddAsync(PipelineRecord<PaperExecutionEvidence> record,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Paper result store is unavailable."));

        public Task<PipelineRecord<PaperExecutionEvidence>?> GetAsync(
            Guid userId, Guid recordId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PipelineRecord<PaperExecutionEvidence>?>(null);

        public Task<IReadOnlyCollection<PipelineRecord<PaperExecutionEvidence>>> ListForUserAsync(
            Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<PipelineRecord<PaperExecutionEvidence>>>([]);

        public Task<IReadOnlyCollection<PipelineRecord<PaperExecutionEvidence>>> ListByCorrelationAsync(
            Guid userId, string correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<PipelineRecord<PaperExecutionEvidence>>>([]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
