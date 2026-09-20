using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class TradePipelineTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid StrategyId = Guid.NewGuid();
    private static readonly string[] OutOfOrderFlag = { "OutOfOrder" };

    private sealed class RecordingAuditWriter : IAuditEventWriter
    {
        private readonly List<AuditEvent> _events = new();

        public IReadOnlyList<AuditEvent> Events => _events;

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            _events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedStrategy : IPipelineStrategy
    {
        private readonly SignalDirection _direction;

        public FixedStrategy(SignalDirection direction) => _direction = direction;

        public Guid StrategyId => TradePipelineTests.StrategyId;

        public StrategyDecision? Evaluate(MarketEvent marketEvent)
        {
            ArgumentNullException.ThrowIfNull(marketEvent);

            return new StrategyDecision(
                Guid.NewGuid(),
                StrategyId,
                marketEvent.Symbol,
                _direction,
                0.8m,
                DateTimeOffset.UtcNow,
                "test signal");
        }
    }

    private sealed class UnknownStatusAdapter : IExecutionAdapter
    {
        public int Calls { get; private set; }

        public Task<ExecutionResult> ExecuteAsync(ExecutionCommand command, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Calls++;

            return Task.FromResult(new ExecutionResult(
                command.Id, false, "Unknown", 0m, 0m, 0m, DateTimeOffset.UtcNow, "Timed out"));
        }
    }

    private sealed class Harness
    {
        public InMemoryMarketEventRepository MarketEvents { get; } = new();

        public InMemoryStrategyDecisionRepository Decisions { get; } = new();

        public InMemoryTradeIntentRepository Intents { get; } = new();

        public InMemoryRiskEvaluationRepository RiskEvaluations { get; } = new();

        public InMemoryExecutionCommandRepository Commands { get; } = new();

        public InMemoryPortfolioUpdateRepository PortfolioUpdates { get; } = new();

        public RecordingAuditWriter Audit { get; } = new();

        public InMemoryTradingHaltState Halts { get; } = new();

        public OrderIdempotencyGuard Idempotency { get; } = new();

        public TradePipeline Build(TradePipelineOptions? options = null) =>
            new(MarketEvents, Decisions, Intents, RiskEvaluations, Commands, PortfolioUpdates,
                Audit, new RiskEngine(), Halts, Idempotency, options);
    }

    private static MarketEvent Event(bool isClosed = true, IReadOnlyCollection<string>? flags = null) =>
        new(Guid.NewGuid(), "BTCUSDT", CandleInterval.OneMinute, DateTimeOffset.UtcNow, 100m, 5m, isClosed, flags);

    private static PipelineContext Context(TradingMode mode = TradingMode.Paper) =>
        new(UserId, mode, "corr-1");

    private static PortfolioSnapshot Portfolio(DateTimeOffset? lastUpdated = null) =>
        new(0m, 0m, 10_000m, 0m, 0, 0, lastUpdated ?? DateTimeOffset.UtcNow);

    [Fact]
    public async Task PaperTradeCompletesFullPipelineAndWritesAudit()
    {
        var harness = new Harness();
        var pipeline = harness.Build();

        var result = await pipeline.ProcessAsync(
            Event(), Context(), new FixedStrategy(SignalDirection.Buy), Portfolio(), new PaperExecutionAdapter());

        Assert.True(result.Executed);
        Assert.Equal(PipelineStage.AuditEvent, result.ReachedStage);
        Assert.NotNull(result.PortfolioUpdate);

        // Every stage was persisted in order.
        Assert.Single(await harness.MarketEvents.ListForUserAsync(UserId));
        Assert.Single(await harness.Decisions.ListForUserAsync(UserId));
        Assert.Single(await harness.Intents.ListForUserAsync(UserId));
        Assert.Single(await harness.RiskEvaluations.ListForUserAsync(UserId));
        Assert.Single(await harness.Commands.ListForUserAsync(UserId));
        Assert.Single(await harness.PortfolioUpdates.ListForUserAsync(UserId));
        Assert.Contains(harness.Audit.Events, e => e.Action == "Trade.PaperExecuted");
    }

    [Fact]
    public async Task LiveTradingIsBlockedByDefault()
    {
        var harness = new Harness();
        var pipeline = harness.Build();

        var result = await pipeline.ProcessAsync(
            Event(), Context(TradingMode.Live), new FixedStrategy(SignalDirection.Buy),
            Portfolio(), new PaperExecutionAdapter());

        Assert.False(result.Executed);
        Assert.Contains("Live trading is disabled", result.BlockedReason, StringComparison.OrdinalIgnoreCase);

        // Nothing progressed past the market event.
        Assert.Empty(await harness.Decisions.ListForUserAsync(UserId));
        Assert.Empty(await harness.Commands.ListForUserAsync(UserId));
    }

    [Fact]
    public async Task UnclosedCandleNeverProducesASignal()
    {
        var harness = new Harness();
        var pipeline = harness.Build();

        var result = await pipeline.ProcessAsync(
            Event(isClosed: false), Context(), new FixedStrategy(SignalDirection.Buy),
            Portfolio(), new PaperExecutionAdapter());

        Assert.False(result.Executed);
        Assert.Contains("closed candle", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Decisions.ListForUserAsync(UserId));
    }

    [Fact]
    public async Task MarketDataQualityIssuesBlockTheTrade()
    {
        var harness = new Harness();
        var pipeline = harness.Build();

        var result = await pipeline.ProcessAsync(
            Event(flags: OutOfOrderFlag), Context(), new FixedStrategy(SignalDirection.Buy),
            Portfolio(), new PaperExecutionAdapter());

        Assert.False(result.Executed);
        Assert.Contains("quality", result.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Commands.ListForUserAsync(UserId));
    }

    [Fact]
    public async Task StaleAccountDataBlocksNewExposure()
    {
        var harness = new Harness();
        var pipeline = harness.Build(new TradePipelineOptions { MaxDataAge = TimeSpan.FromMinutes(1) });

        var result = await pipeline.ProcessAsync(
            Event(), Context(), new FixedStrategy(SignalDirection.Buy),
            Portfolio(lastUpdated: DateTimeOffset.UtcNow.AddHours(-2)), new PaperExecutionAdapter());

        Assert.False(result.Executed);
        Assert.Equal(PipelineStage.RiskEvaluation, result.ReachedStage);
        Assert.Contains("stale", result.BlockedReason, StringComparison.OrdinalIgnoreCase);

        // The risk evaluation was recorded even though the trade was denied.
        Assert.Single(await harness.RiskEvaluations.ListForUserAsync(UserId));
        Assert.Empty(await harness.Commands.ListForUserAsync(UserId));
    }

    [Fact]
    public async Task RiskEngineDenialStopsThePipelineBeforeExecution()
    {
        var harness = new Harness();
        var pipeline = harness.Build(new TradePipelineOptions { MaxPositionSize = 1m, MaxNotional = 1m });

        var result = await pipeline.ProcessAsync(
            Event(), Context(), new FixedStrategy(SignalDirection.Buy), Portfolio(), new PaperExecutionAdapter());

        Assert.False(result.Executed);
        Assert.Equal(PipelineStage.RiskEvaluation, result.ReachedStage);
        Assert.Empty(await harness.Commands.ListForUserAsync(UserId));
        Assert.Contains(harness.Audit.Events, e => e.Action.StartsWith("Trade.Blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HoldSignalDoesNotCreateATradeIntent()
    {
        var harness = new Harness();
        var pipeline = harness.Build();

        var result = await pipeline.ProcessAsync(
            Event(), Context(), new FixedStrategy(SignalDirection.Hold), Portfolio(), new PaperExecutionAdapter());

        Assert.False(result.Executed);
        Assert.Equal(PipelineStage.StrategyDecision, result.ReachedStage);
        Assert.Empty(await harness.Intents.ListForUserAsync(UserId));
    }

    [Fact]
    public async Task UnknownExchangeStatusRequiresReconciliationAndIsNotRetried()
    {
        var harness = new Harness();
        var pipeline = harness.Build();
        var adapter = new UnknownStatusAdapter();

        var result = await pipeline.ProcessAsync(
            Event(), Context(), new FixedStrategy(SignalDirection.Buy), Portfolio(), adapter);

        Assert.False(result.Executed);
        Assert.True(result.RequiresReconciliation);
        Assert.Equal(1, adapter.Calls);
        Assert.Empty(await harness.PortfolioUpdates.ListForUserAsync(UserId));
        Assert.Contains(harness.Audit.Events, e => e.Action == "Trade.ExecutionUnknown");
    }

    [Fact]
    public async Task ClientOrderIdIsDeterministicForTheSameIntent()
    {
        var harness = new Harness();
        var pipeline = harness.Build();

        await pipeline.ProcessAsync(
            Event(), Context(), new FixedStrategy(SignalDirection.Buy), Portfolio(), new PaperExecutionAdapter());

        var command = (await harness.Commands.ListForUserAsync(UserId)).Single().Payload;

        Assert.StartsWith("paper-", command.ClientOrderId, StringComparison.Ordinal);
        Assert.Contains(command.TradeIntentId.ToString("N"), command.ClientOrderId, StringComparison.Ordinal);
        Assert.True(command.IsPaperOnly);
    }

    [Fact]
    public async Task PipelineRecordsAreScopedToTheOwningUser()
    {
        var harness = new Harness();
        var pipeline = harness.Build();
        var otherUser = Guid.NewGuid();

        await pipeline.ProcessAsync(
            Event(), Context(), new FixedStrategy(SignalDirection.Buy), Portfolio(), new PaperExecutionAdapter());

        Assert.NotEmpty(await harness.Commands.ListForUserAsync(UserId));
        Assert.Empty(await harness.Commands.ListForUserAsync(otherUser));
        Assert.Empty(await harness.PortfolioUpdates.ListForUserAsync(otherUser));
    }
}
