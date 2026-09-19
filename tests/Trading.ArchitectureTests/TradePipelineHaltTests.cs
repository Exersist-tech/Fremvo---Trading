using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Risk;

namespace Trading.ArchitectureTests;

/// <summary>
/// Proves that every halt scope actually stops a trade in the pipeline, rather than merely being
/// representable in the risk model.
/// </summary>
public sealed class TradePipelineHaltTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();
    private static readonly Guid StrategyId = Guid.NewGuid();

    private sealed class SilentAuditWriter : IAuditEventWriter
    {
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class BuyStrategy : IPipelineStrategy
    {
        public Guid StrategyId => TradePipelineHaltTests.StrategyId;

        public StrategyDecision? Evaluate(MarketEvent marketEvent)
        {
            ArgumentNullException.ThrowIfNull(marketEvent);
            return new StrategyDecision(
                Guid.NewGuid(), StrategyId, marketEvent.Symbol, SignalDirection.Buy, 0.9m,
                marketEvent.EventTimeUtc, "test");
        }
    }

    private sealed class SellStrategy : IPipelineStrategy
    {
        public Guid StrategyId => TradePipelineHaltTests.StrategyId;

        public StrategyDecision? Evaluate(MarketEvent marketEvent)
        {
            ArgumentNullException.ThrowIfNull(marketEvent);
            return new StrategyDecision(
                Guid.NewGuid(), StrategyId, marketEvent.Symbol, SignalDirection.Sell, 0.9m,
                marketEvent.EventTimeUtc, "test");
        }
    }

    private static TradePipeline Build(InMemoryTradingHaltState halts, OrderIdempotencyGuard? guard = null) =>
        new(new InMemoryMarketEventRepository(),
            new InMemoryStrategyDecisionRepository(),
            new InMemoryTradeIntentRepository(),
            new InMemoryRiskEvaluationRepository(),
            new InMemoryExecutionCommandRepository(),
            new InMemoryPortfolioUpdateRepository(),
            new SilentAuditWriter(),
            new RiskEngine(),
            halts,
            guard ?? new OrderIdempotencyGuard());

    private static MarketEvent Event() =>
        new(Guid.NewGuid(), "BTCUSDT", CandleInterval.OneMinute, DateTimeOffset.UtcNow, 100m, 5m, true);

    private static PipelineContext Context(Guid? userId = null) =>
        new(userId ?? UserId, TradingMode.Paper, "corr-halt");

    private static PortfolioSnapshot Portfolio(decimal positionQuantity = 0m) =>
        new(positionQuantity * 100m, positionQuantity, 10_000m, 0m, 0, positionQuantity == 0m ? 0 : 1,
            DateTimeOffset.UtcNow);

    private static Task<TradePipelineResult> RunAsync(
        TradePipeline pipeline,
        IPipelineStrategy? strategy = null,
        Guid? userId = null,
        decimal positionQuantity = 0m) =>
        pipeline.ProcessAsync(
            Event(), Context(userId), strategy ?? new BuyStrategy(),
            Portfolio(positionQuantity), new PaperExecutionAdapter());

    [Fact]
    public async Task TheEmergencyStopBlocksTheTrade()
    {
        var halts = new InMemoryTradingHaltState();
        halts.EngageEmergencyStop();

        var result = await RunAsync(Build(halts));

        Assert.False(result.Executed);
        Assert.Equal(PipelineStage.RiskEvaluation, result.ReachedStage);
    }

    [Fact]
    public async Task ReleasingTheEmergencyStopAllowsTradingAgain()
    {
        var halts = new InMemoryTradingHaltState();
        halts.EngageEmergencyStop();
        halts.ReleaseEmergencyStop();

        var result = await RunAsync(Build(halts));

        Assert.True(result.Executed);
    }

    [Fact]
    public async Task AMarketHaltBlocksTheTrade()
    {
        var halts = new InMemoryTradingHaltState();
        halts.HaltMarket("BTCUSDT");

        var result = await RunAsync(Build(halts));

        Assert.False(result.Executed);
    }

    [Fact]
    public async Task AMarketHaltOnAnotherSymbolDoesNotBlockThisTrade()
    {
        var halts = new InMemoryTradingHaltState();
        halts.HaltMarket("ETHUSDT");

        var result = await RunAsync(Build(halts));

        Assert.True(result.Executed);
    }

    [Fact]
    public async Task AUserHaltBlocksOnlyThatUser()
    {
        var halts = new InMemoryTradingHaltState();
        halts.HaltUser(UserId);

        Assert.False((await RunAsync(Build(halts), userId: UserId)).Executed);
        Assert.True((await RunAsync(Build(halts), userId: OtherUserId)).Executed);
    }

    [Fact]
    public async Task AStrategyHaltBlocksTheTrade()
    {
        var halts = new InMemoryTradingHaltState();
        halts.HaltStrategy(StrategyId);

        var result = await RunAsync(Build(halts));

        Assert.False(result.Executed);
    }

    [Fact]
    public async Task CloseOnlyModeBlocksANewPositionButAllowsClosingOne()
    {
        var halts = new InMemoryTradingHaltState();
        halts.SetCloseOnly(UserId, true);

        var opening = await RunAsync(Build(halts), new BuyStrategy());
        Assert.False(opening.Executed);

        var closing = await RunAsync(Build(halts), new SellStrategy(), positionQuantity: 5m);
        Assert.True(closing.Executed);
    }

    [Fact]
    public async Task ReduceOnlyModeBlocksAnExposureIncrease()
    {
        var halts = new InMemoryTradingHaltState();
        halts.SetReduceOnly(UserId, true);

        var increasing = await RunAsync(Build(halts), new BuyStrategy(), positionQuantity: 5m);
        Assert.False(increasing.Executed);

        var reducing = await RunAsync(Build(halts), new SellStrategy(), positionQuantity: 5m);
        Assert.True(reducing.Executed);
    }

    [Fact]
    public void ReplayingTheSameIntentIsRejectedAsADuplicateInsteadOfPlacingASecondOrder()
    {
        var guard = new OrderIdempotencyGuard();

        // The same client order id, symbol, quantity, and price registered twice must not
        // produce two orders.
        var first = guard.RegisterOrCheck("paper-abc", "BTCUSDT", 1m, 100m);
        var second = guard.RegisterOrCheck("paper-abc", "BTCUSDT", 1m, 100m);

        Assert.True(first.IsAccepted);
        Assert.True(second.IsDuplicate);
        Assert.False(second.IsAccepted);

        // A conflicting payload under the same id is neither accepted nor a plain duplicate.
        var conflict = guard.RegisterOrCheck("paper-abc", "BTCUSDT", 2m, 100m);
        Assert.True(conflict.IsConflict);
        Assert.False(conflict.IsAccepted);
        Assert.False(conflict.IsDuplicate);
    }

    [Fact]
    public async Task AHaltIsEvaluatedForTheStrategyThatActuallyRuns()
    {
        var halts = new InMemoryTradingHaltState();
        halts.HaltStrategy(Guid.NewGuid());

        var result = await RunAsync(Build(halts));

        Assert.True(result.Executed);
    }
}
