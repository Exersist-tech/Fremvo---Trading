using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class PaperExperimentWorkerRunnerTests
{
    // The pipeline stamps records with the real UTC clock, so the test clock must track it;
    // otherwise every event reads as stale and nothing reaches execution.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class SingleEventFeed : IExperimentMarketFeed
    {
        private readonly MarketEvent? _marketEvent;
        private bool _served;

        public SingleEventFeed(MarketEvent? marketEvent) => _marketEvent = marketEvent;

        public Task<MarketEvent?> TryGetNextClosedEventAsync(Guid workerId, string symbol, CancellationToken cancellationToken)
        {
            if (_served)
            {
                return Task.FromResult<MarketEvent?>(null);
            }

            _served = true;
            return Task.FromResult(_marketEvent);
        }
    }

    private sealed class FixedTemplateFactory : IApprovedStrategyTemplateFactory
    {
        private readonly IPipelineStrategy? _strategy;

        public FixedTemplateFactory(IPipelineStrategy? strategy) => _strategy = strategy;

        public IPipelineStrategy? TryCreate(string strategyTemplateId, string parametersJson) => _strategy;
    }

    private sealed class FixedStrategy : IPipelineStrategy
    {
        private readonly SignalDirection _direction;

        public FixedStrategy(SignalDirection direction) => _direction = direction;

        public Guid StrategyId { get; } = Guid.NewGuid();

        public StrategyDecision? Evaluate(MarketEvent marketEvent) => new(
            Guid.NewGuid(),
            StrategyId,
            marketEvent.Symbol,
            _direction,
            0.9m,
            marketEvent.EventTimeUtc,
            "test");
    }

    private static MarketEvent Candle(bool isClosed = true) => new(
        Guid.NewGuid(),
        "BTCUSDT",
        CandleInterval.OneMinute,
        Now,
        lastPrice: 100m,
        volume: 10m,
        isClosed: isClosed);

    private static PaperExperimentWorkerRunner Runner(
        MarketEvent? marketEvent,
        IPipelineStrategy? strategy,
        IExperimentWorkerRepository repository)
    {
        var pipeline = new TradePipeline(
            new InMemoryMarketEventRepository(),
            new InMemoryStrategyDecisionRepository(),
            new InMemoryTradeIntentRepository(),
            new InMemoryRiskEvaluationRepository(),
            new InMemoryExecutionCommandRepository(),
            new InMemoryPortfolioUpdateRepository(),
            new InMemoryAuditEventWriter(),
            new RiskEngine(),
            new TradePipelineOptions { MaxDataAge = TimeSpan.FromHours(24) });

        return new PaperExperimentWorkerRunner(
            pipeline,
            new SingleEventFeed(marketEvent),
            new FixedTemplateFactory(strategy),
            new PaperExecutionAdapter(),
            repository,
            new FakeTimeProvider(Now));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task<ExperimentWorker> RunningWorkerAsync(InMemoryExperimentWorkerRepository repository)
    {
        var worker = new ExperimentWorker(
            Guid.NewGuid(), Guid.NewGuid(), "w", "template-sma", "BTCUSDT", 10_000m, Now, randomSeed: 7);
        worker.Start();
        await repository.SaveAsync(worker);
        return worker;
    }

    [Fact]
    public async Task ABuySignalIsAppliedToTheWorkersOwnLedger()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var worker = await RunningWorkerAsync(repository);

        var runner = Runner(Candle(), new FixedStrategy(SignalDirection.Buy), repository);
        await runner.RunOnceAsync(worker, CancellationToken.None);

        Assert.Equal(1m, worker.PositionQuantity);
        Assert.Equal(100m, worker.AverageEntryPrice);
        Assert.Single(worker.Ledger);
        Assert.True(worker.CashBalance < 10_000m);
    }

    [Fact]
    public async Task AnUnclosedCandleNeverProducesATrade()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var worker = await RunningWorkerAsync(repository);

        var runner = Runner(Candle(isClosed: false), new FixedStrategy(SignalDirection.Buy), repository);
        await runner.RunOnceAsync(worker, CancellationToken.None);

        Assert.Equal(0m, worker.PositionQuantity);
        Assert.Empty(worker.Ledger);
        Assert.Equal(10_000m, worker.CashBalance);
    }

    [Fact]
    public async Task NoMarketDataLeavesTheWorkerIdleRatherThanFaulted()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var worker = await RunningWorkerAsync(repository);

        var runner = Runner(null, strategy: null, repository);
        await runner.RunOnceAsync(worker, CancellationToken.None);

        Assert.Equal(ExperimentWorkerStatus.Running, worker.Status);
        Assert.Empty(worker.Ledger);
    }

    [Fact]
    public async Task AnUnapprovedStrategyTemplateFaultsTheWorkerInsteadOfTrading()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var worker = await RunningWorkerAsync(repository);

        var runner = Runner(Candle(), strategy: null, repository);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunOnceAsync(worker, CancellationToken.None));

        Assert.Contains("not approved", ex.Message, StringComparison.Ordinal);
        Assert.Empty(worker.Ledger);
    }

    [Fact]
    public async Task ASellSignalWithNoPositionDoesNotCreateAShortInAPaperExperiment()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var worker = await RunningWorkerAsync(repository);

        var runner = Runner(Candle(), new FixedStrategy(SignalDirection.Sell), repository);

        // Spot paper experiments cannot go short; the worker refuses rather than inventing one.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunOnceAsync(worker, CancellationToken.None));

        Assert.Equal(0m, worker.PositionQuantity);
    }
}
