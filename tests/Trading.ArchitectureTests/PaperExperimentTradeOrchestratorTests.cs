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
        Assert.Single(await harness.Portfolios.ListForUserAsync(proposal.Key.UserId));
        Assert.Contains(harness.Audit.Events, e => e.Action == "Trade.PaperExecuted");
        Assert.All(await harness.Commands.ListForUserAsync(proposal.Key.UserId), command => Assert.True(command.Payload.IsPaperOnly));
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
        var retry = await unknownOrchestrator.ProcessAsync(unknown, unknownContext);
        Assert.False(retry.Submitted);
        Assert.Empty(unknownAdapter.Ledger);
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
        public InMemoryPortfolioUpdateRepository Portfolios { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public InMemoryExperimentDecisionLedger DecisionsLedger { get; } = new();
        public InMemoryExperimentPaperExecutionLedger Executions { get; } = new();
        private readonly decimal _maxNotional;
        public Harness(decimal maxNotional = 1_000m) => _maxNotional = maxNotional;

        public (PaperExperimentTradeOrchestrator, ExperimentDecisionRecord, ExperimentPaperWorkerContext, PaperExecutionAdapter) Create(
            ExperimentProposalAction action, decimal position = 0m)
        {
            var user = Guid.NewGuid();
            var worker = new ExperimentWorker(Guid.NewGuid(), user, "paper-worker", "experiment-sma-trend", "BTC/USD", 1_000m, Now, 1);
            var key = new ExperimentDecisionKey(user, worker.Id, 1, ExperimentResearchGroup.A, worker.StrategyId, 1,
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", worker.MarketSymbol, CandleInterval.OneHour,
                Now.AddHours(-1), Now, Now);
            var proposal = new ExperimentDecisionRecord(key, new ExperimentProposal(action, "approved test proposal"), "evidence", Now);
            DecisionsLedger.RecordAsync(user, proposal).GetAwaiter().GetResult();
            var pipeline = new TradePipeline(Events, Decisions, Intents, Risks, Commands, Portfolios, Audit,
                new RiskEngine(), new InMemoryTradingHaltState(), new OrderIdempotencyGuard(),
                new TradePipelineOptions { MaxNotional = _maxNotional, MaxPositionSize = 100m },
                timeProvider: new FixedTimeProvider(Now));
            var adapter = new PaperExecutionAdapter();
            var orchestrator = new PaperExperimentTradeOrchestrator(DecisionsLedger, Executions, pipeline, adapter);
            var candle = new ExperimentPaperCandleSnapshot(worker.MarketSymbol, CandleInterval.OneHour, key.OpenTimeUtc, key.CloseTimeUtc,
                key.AsOfUtc, 100m, 1m);
            return (orchestrator, proposal, new ExperimentPaperWorkerContext(worker,
                new ExperimentWorkerPortfolioSnapshot(user, worker.Id, position, Now), candle), adapter);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
