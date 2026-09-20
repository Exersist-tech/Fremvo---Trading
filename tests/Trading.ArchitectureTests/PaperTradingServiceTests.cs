using Trading.Application.Execution;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Market;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Exchanges.Abstractions;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTradingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string Symbol = "XBTUSD";

    private sealed class Harness
    {
        public Harness(IReadOnlyList<Candle> candles, bool exchangeConnected = true)
        {
            Candles = new StubCandleSource(candles);
            Orders = new InMemoryOrderRepository();
            Positions = new InMemoryPositionRepository();
            Halts = new InMemoryTradingHaltState();
            Audit = new RecordingAuditWriter();
            ExchangeAccounts = new StubExchangeAccounts();

            if (exchangeConnected)
            {
                ExchangeAccounts.AddConnected(UserId, Now);
                ExchangeAccounts.AddConnected(OtherUserId, Now);
            }

            Service = new PaperTradingService(
                Candles,
                Orders,
                Positions,
                Halts,
                Audit,
                ExchangeAccounts,
                new FixedTimeProvider(Now));
        }

        public StubCandleSource Candles { get; }

        public InMemoryOrderRepository Orders { get; }

        public InMemoryPositionRepository Positions { get; }

        public InMemoryTradingHaltState Halts { get; }

        public RecordingAuditWriter Audit { get; }

        public StubExchangeAccounts ExchangeAccounts { get; }

        public PaperTradingService Service { get; }
    }

    private sealed class StubExchangeAccounts : IExchangeAccountRepository
    {
        private readonly List<ExchangeAccount> _accounts = new();

        public void AddConnected(Guid userId, DateTimeOffset nowUtc)
        {
            var account = new ExchangeAccount(
                Guid.NewGuid(),
                userId,
                ExchangeKind.Kraken,
                "Kraken",
                "exchange-credential/test",
                nowUtc);
            account.MarkConnected(nowUtc);
            _accounts.Add(account);
        }

        public void AddUnvalidated(Guid userId, DateTimeOffset nowUtc) =>
            _accounts.Add(new ExchangeAccount(
                Guid.NewGuid(),
                userId,
                ExchangeKind.Kraken,
                "Kraken",
                "exchange-credential/test",
                nowUtc));

        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_accounts.Find(account => account.Id == id));

        public Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ExchangeAccount>>(
                _accounts.FindAll(account => account.UserId == userId));

        public Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default)
        {
            _accounts.Add(account);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static Candle ClosedCandle(DateTimeOffset closeTime, decimal close) =>
        new(
            Symbol,
            CandleInterval.OneMinute,
            closeTime.AddMinutes(-1),
            closeTime,
            close,
            close,
            close,
            close,
            volume: 1m,
            isClosed: true,
            isDerived: false);

    private static Candle FormingCandle(DateTimeOffset closeTime, decimal close) =>
        new(
            Symbol,
            CandleInterval.OneMinute,
            closeTime.AddMinutes(-1),
            closeTime,
            close,
            close,
            close,
            close,
            volume: 1m,
            isClosed: false,
            isDerived: false);

    private static Harness FreshMarket(decimal lastClose = 30000m) =>
        new(
        [
            ClosedCandle(Now.AddMinutes(-2), lastClose - 100m),
            ClosedCandle(Now.AddMinutes(-1), lastClose),
            FormingCandle(Now.AddMinutes(1), lastClose + 5000m),
        ]);

    private static Harness MarketWithoutExchangeAccount(decimal lastClose = 30000m) =>
        new(
            [
                ClosedCandle(Now.AddMinutes(-2), lastClose - 100m),
                ClosedCandle(Now.AddMinutes(-1), lastClose),
            ],
            exchangeConnected: false);

    [Fact]
    public async Task AnOrderIsRefusedWhenNoExchangeAccountIsConnected()
    {
        var harness = MarketWithoutExchangeAccount();

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 0.5m, null);

        Assert.False(result.Succeeded);
        Assert.Equal(PaperTradeOutcome.NoConnectedExchange, result.Outcome);

        // Nothing may be recorded either: a refused order must leave no order
        // and no position behind.
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
        Assert.Empty(await harness.Positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task AnAccountThatWasNeverValidatedDoesNotUnlockTrading()
    {
        var harness = MarketWithoutExchangeAccount();

        // Present but never validated: the row exists, the connection was never
        // proven. Accepting this would let an unusable key unlock trading.
        harness.ExchangeAccounts.AddUnvalidated(UserId, Now);

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 0.5m, null);

        Assert.Equal(PaperTradeOutcome.NoConnectedExchange, result.Outcome);
    }

    [Fact]
    public async Task AnotherUsersConnectedAccountDoesNotUnlockTrading()
    {
        var harness = MarketWithoutExchangeAccount();
        harness.ExchangeAccounts.AddConnected(OtherUserId, Now);

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 0.5m, null);

        Assert.Equal(PaperTradeOutcome.NoConnectedExchange, result.Outcome);
    }

    [Fact]
    public async Task AConnectedAccountStillOnlyPermitsSimulatedFills()
    {
        var harness = FreshMarket();

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 0.5m, null);

        // Connecting an exchange lifts the gate on simulation. It must never
        // also promote the account out of paper, or a connection alone would
        // become permission to trade real funds.
        Assert.True(result.Succeeded);
        var accounts = await harness.ExchangeAccounts.ListForUserAsync(UserId);
        Assert.All(accounts, account => Assert.False(account.CanReachExchange));
    }

    [Fact]
    public async Task FillIsPricedFromTheLastClosedCandleNotTheFormingOne()
    {
        var harness = FreshMarket(lastClose: 30000m);

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 0.5m, null);

        Assert.True(result.Succeeded);

        // The forming bar in the fixture prints 35000. Using it would price the
        // fill against a close that has not settled.
        Assert.Equal(30000m, result.Order!.Price);
        Assert.Equal(30000m, result.Position!.EntryPrice);
    }

    [Fact]
    public async Task AnOrderWithNoClosedCandleIsRefused()
    {
        var harness = new Harness([FormingCandle(Now.AddMinutes(1), 30000m)]);

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);

        Assert.Equal(PaperTradeOutcome.PriceUnavailable, result.Outcome);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task AnOrderIsBlockedWhenTheLastClosedCandleIsStale()
    {
        // Paper trading is not an exemption from the staleness rule: a fill
        // against a price that stopped updating teaches the wrong thing.
        var harness = new Harness([ClosedCandle(Now.AddHours(-3), 30000m)]);

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);

        Assert.Equal(PaperTradeOutcome.PriceStale, result.Outcome);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task AHaltBlocksTheOrderAndStoresNothing()
    {
        var harness = FreshMarket();
        harness.Halts.EngageEmergencyStop();

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);

        Assert.Equal(PaperTradeOutcome.Blocked, result.Outcome);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
        Assert.Empty(await harness.Positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task AUserHaltBlocksOnlyThatUser()
    {
        var harness = FreshMarket();
        harness.Halts.HaltUser(UserId);

        var blocked = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);
        var allowed = await harness.Service.SubmitAsync(OtherUserId, Symbol, OrderSide.Buy, 1m, null);

        Assert.Equal(PaperTradeOutcome.Blocked, blocked.Outcome);
        Assert.True(allowed.Succeeded);
    }

    [Fact]
    public async Task CloseOnlyModeBlocksOpeningButPermitsReducing()
    {
        var harness = FreshMarket();

        var opened = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 2m, null);
        Assert.True(opened.Succeeded);

        harness.Halts.SetCloseOnly(UserId, true);

        // Judged by what the order does to the position, not by its side.
        var increase = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);
        Assert.Equal(PaperTradeOutcome.Blocked, increase.Outcome);

        var reduce = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Sell, 1m, null);
        Assert.True(reduce.Succeeded);
    }

    [Fact]
    public async Task TwoOrdersInTheSameMillisecondAreBothAccepted()
    {
        // The time provider here is fixed, which is exactly the production case
        // of two submissions landing in the same millisecond. A generated
        // client order id derived only from the clock would collide and refuse
        // the second order as a duplicate, silently dropping a real intent.
        var harness = FreshMarket();

        var first = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);
        var second = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Sell, 1m, null);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(2, (await harness.Orders.ListAsync(UserId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ARepeatedClientOrderIdIsRefusedAndCreatesNoSecondOrder()
    {
        var harness = FreshMarket();

        var first = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, "repeat-me");
        var second = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, "repeat-me");

        Assert.True(first.Succeeded);
        Assert.Equal(PaperTradeOutcome.Duplicate, second.Outcome);
        Assert.Single(await harness.Orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task AFilledOrderReachesTheFilledStateWithTheFullQuantity()
    {
        var harness = FreshMarket();

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1.5m, null);

        Assert.Equal(OrderState.Filled, result.Order!.State);
        Assert.Equal(1.5m, result.Order.FilledQuantity);
        Assert.Equal(0m, result.Order.RemainingQuantity);
        Assert.False(result.Order.RequiresReconciliation);
    }

    [Fact]
    public async Task AnOppositeOrderReducesThePositionAndClosesItWhenFullySold()
    {
        var harness = FreshMarket();

        await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 2m, null);

        var partial = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Sell, 1m, null);
        Assert.True(partial.Succeeded);
        Assert.Equal(1m, partial.Position!.Quantity);
        Assert.Equal(PositionStatus.Open, partial.Position.Status);

        var closed = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Sell, 1m, null);
        Assert.True(closed.Succeeded);
        Assert.Equal(0m, closed.Position!.Quantity);
        Assert.Equal(PositionStatus.Flat, closed.Position.Status);

        Assert.Empty(await harness.Positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task IncreasingAnOpenPositionIsRefusedRatherThanInventingACostBasis()
    {
        var harness = FreshMarket();

        await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);
        var increase = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);

        // Averaging in would need a cost basis the position model does not
        // carry. A fabricated entry price would misstate every profit and loss
        // figure derived from it.
        Assert.Equal(PaperTradeOutcome.Rejected, increase.Outcome);
        Assert.Single(await harness.Positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ReducingByMoreThanTheOpenQuantityIsRefused()
    {
        var harness = FreshMarket();

        await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);
        var oversized = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Sell, 5m, null);

        // Otherwise the reduction would flip into a short position the user
        // never asked for.
        Assert.Equal(PaperTradeOutcome.Rejected, oversized.Outcome);

        var open = await harness.Positions.ListOpenAsync(UserId, CancellationToken.None);
        Assert.Equal(1m, Assert.Single(open).Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ANonPositiveQuantityIsRefused(int quantity)
    {
        var harness = FreshMarket();

        var result = await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, quantity, null);

        Assert.Equal(PaperTradeOutcome.Rejected, result.Outcome);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task OnePlayersOrdersAreInvisibleToAnother()
    {
        var harness = FreshMarket();

        await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);

        Assert.Empty(await harness.Orders.ListAsync(OtherUserId, CancellationToken.None));
        Assert.Empty(await harness.Positions.ListOpenAsync(OtherUserId, CancellationToken.None));
    }

    [Fact]
    public async Task AFillWritesAnAuditEventThatNamesTheSimulation()
    {
        var harness = FreshMarket();

        await harness.Service.SubmitAsync(UserId, Symbol, OrderSide.Buy, 1m, null);

        var recorded = Assert.Single(harness.Audit.Events);
        Assert.Equal("PaperOrderFilled", recorded.Action);
        Assert.Equal(UserId, recorded.ActorUserId);

        // An audit trail that does not distinguish simulated from real activity
        // is worthless when reconstructing what happened.
        Assert.Contains("No exchange was contacted", recorded.After, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePaperServiceExposesNoWayToReachAnExchange()
    {
        var constructorParameters = typeof(PaperTradingService)
            .GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType.Name)
            .ToList();

        // Nothing that can place a real order may be injectable here. The class
        // must not be convertible into a live path by wiring alone.
        Assert.DoesNotContain(constructorParameters, name =>
            name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ExecutionAdapter", StringComparison.OrdinalIgnoreCase)
            || name.Contains("OrderGateway", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kraken", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class StubCandleSource : IHistoricalCandleSource
    {
        private readonly IReadOnlyList<Candle> _candles;

        public StubCandleSource(IReadOnlyList<Candle> candles) => _candles = candles;

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_candles);
    }

    private sealed class RecordingAuditWriter : IAuditEventWriter
    {
        private readonly List<AuditEvent> _events = [];

        public IReadOnlyList<AuditEvent> Events => _events;

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            _events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
