using Trading.Application.Execution;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Exchanges.Abstractions;
using Trading.MarketData;
using Trading.Risk;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers the only path in the platform that can move a user's real money.
/// </summary>
/// <remarks>
/// The tests that matter most here are the ones that assert a refusal. An
/// accepted order is easy to get right; what protects a user is that the
/// service declines to send one when ownership, stage, halts, price freshness,
/// duplicate protection or a risk ceiling says it must not.
/// </remarks>
public sealed class LiveTradingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string Symbol = "XBTUSD";

    [Fact]
    public async Task AnAcceptedOrderIsRecordedAsWorkingAndNotAsFilled()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-1");

        Assert.Equal(LiveTradeOutcome.Accepted, result.Outcome);

        // Acceptance is not a fill. If this ever becomes Filled, the portfolio
        // would show a position the exchange has not given the user.
        Assert.Equal(0m, result.Order!.FilledQuantity);
        Assert.Equal(TradingMode.Live, result.Order.Mode);
        Assert.Empty(await harness.Positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task AGeneratedLiveClientOrderIdFitsTheDurableColumn()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, clientOrderId: null);

        Assert.Equal(LiveTradeOutcome.Accepted, result.Outcome);
        Assert.True(result.Order!.ClientOrderId.Length <= Order.MaximumClientOrderIdLength);
        Assert.True(Guid.TryParseExact(result.Order.ClientOrderId, "D", out _));
        Assert.Equal('4', result.Order.ClientOrderId[14]);
    }

    [Fact]
    public async Task ALiveOrderIsAlwaysALimitOrder()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-limit");

        // A market order on a thin book fills at whatever is there. A limit
        // that does not fill is a disappointment; a market order that fills
        // badly is a loss the user never agreed to.
        Assert.Equal(OrderType.Limit, result.Order!.Type);
        Assert.Equal(30000m, result.Order.Price);
    }

    [Fact]
    public async Task AnAccountBelongingToAnotherUserIsNotUsable()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            OtherUserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-2");

        // Reported as ineligible rather than forbidden, so this route cannot
        // be used to discover which account identifiers exist.
        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AUserOutsideTheLiveRolloutCohortCannotSubmit()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Options.AllowedUserIds = Array.Empty<Guid>();

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-cohort");

        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AnAccountStillInPaperStageCannotSendARealOrder()
    {
        var harness = new Harness(TradingStage.Paper, accountConnected: false);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-3");

        Assert.Equal(LiveTradeOutcome.AccountNotEligible, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AHaltStopsALiveOrderBeforeTheExchangeIsContacted()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Halts.EngageEmergencyStop();

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-4");

        Assert.Equal(LiveTradeOutcome.Blocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task StalePriceDataBlocksALiveOrder()
    {
        // The most recent settled candle is hours old, so nothing here is a
        // current price. Sizing an order against it would be guessing.
        var harness = new Harness(
            TradingStage.Live,
            candles: [ClosedCandle(Now.AddHours(-5), 30000m)]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-5");

        Assert.Equal(LiveTradeOutcome.PriceUnavailable, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task FuturePriceDataBlocksALiveOrderBeforePersistenceOrAdapterSubmission()
    {
        var harness = new Harness(
            TradingStage.Live,
            candles: [ClosedCandle(Now.AddMinutes(1), 30000m)]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-future-price");

        Assert.Equal(LiveTradeOutcome.PriceUnavailable, result.Outcome);
        Assert.Empty(await harness.Orders.ListAsync(UserId, CancellationToken.None));
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AFormingCandleIsNeverUsedAsAReferencePrice()
    {
        var harness = new Harness(
            TradingStage.Live,
            candles:
            [
                ClosedCandle(Now.AddMinutes(-1), 30000m),
                FormingCandle(Now.AddMinutes(1), 41000m),
            ]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-forming");

        Assert.Equal(30000m, result.Order!.Price);
    }

    [Fact]
    public async Task ARepeatedClientOrderIdIsRefusedRatherThanResubmitted()
    {
        var harness = new Harness(TradingStage.Live);

        await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "same-id");
        var second = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "same-id");

        Assert.Equal(LiveTradeOutcome.Duplicate, second.Outcome);

        // One call, not two. A second submission under the same identifier is
        // how one intended position becomes two.
        Assert.Equal(1, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AnOrderAboveThePlatformNotionalCeilingIsBlocked()
    {
        var harness = new Harness(TradingStage.Live);

        // 30000 * 1 is far above the default ceiling.
        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 1m, "live-6");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AProvingAccountCannotTradeASymbolThatWasNeverApproved()
    {
        var harness = new Harness(TradingStage.Proving);
        harness.Account.SetProvingNotionalCeiling(1000m);

        // ProvingSymbols is empty by default, so nothing is approved. "Not
        // configured" must read as "nothing approved", never as "anything".
        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-7");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AProvingAccountWithNoCeilingCannotTrade()
    {
        var harness = new Harness(TradingStage.Proving);
        harness.Options.ProvingSymbols = [Symbol];
        harness.Account.ClearProvingNotionalCeiling();

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-8");

        Assert.Equal(LiveTradeOutcome.RiskBlocked, result.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AProvingAccountMayTradeAnApprovedSymbolWithinItsCeiling()
    {
        var harness = new Harness(TradingStage.Proving);
        harness.Options.ProvingSymbols = [Symbol];
        harness.Account.SetProvingNotionalCeiling(50m);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-9");

        Assert.Equal(LiveTradeOutcome.Accepted, result.Outcome);
    }

    [Fact]
    public async Task AnExchangeRefusalIsRecordedAsRejectedAndNotAsUnknown()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Adapter.Result = ExecutionOutcome.Rejected;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-10");

        Assert.Equal(LiveTradeOutcome.Rejected, result.Outcome);
        Assert.False(result.RequiresReconciliation);
        Assert.Equal(OrderState.Rejected, result.Order!.State);
        Assert.Empty(await harness.Reconciliations.ListUnresolvedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUnansweredSubmissionIsFrozenForReconciliationRatherThanRetried()
    {
        var harness = new Harness(TradingStage.Live);
        harness.Adapter.Result = ExecutionOutcome.Unknown;

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-11");

        Assert.Equal(LiveTradeOutcome.Unknown, result.Outcome);
        Assert.True(result.RequiresReconciliation);

        // The order exists locally under the identifier the exchange may also
        // know, and a reconciliation record was opened. Neither the service nor
        // the caller may resubmit until that record is resolved.
        Assert.Single(await harness.Reconciliations.ListUnresolvedAsync(CancellationToken.None));
        Assert.NotNull(await harness.Orders.FindByClientOrderIdAsync("live-11", CancellationToken.None));
        Assert.Contains("Do not resubmit", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOrderIsStoredBeforeTheExchangeIsContacted()
    {
        var harness = new Harness(TradingStage.Live);
        Order? seenDuringCall = null;
        harness.Adapter.OnExecute = async () =>
            seenDuringCall = await harness.Orders.FindByClientOrderIdAsync("live-12", CancellationToken.None);

        await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-12");

        // A crash between sending and recording would otherwise leave an order
        // at the exchange with no local handle by which to find it.
        Assert.NotNull(seenDuringCall);
    }

    [Fact]
    public async Task ALiveOrderIsNeverWrittenIntoThePaperBook()
    {
        var harness = new Harness(TradingStage.Live);

        await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-13");
        var stored = await harness.Orders.ListAsync(UserId, CancellationToken.None);

        Assert.All(stored, order => Assert.Equal(TradingMode.Live, order.Mode));
    }

    [Fact]
    public async Task ZeroAndNegativeQuantitiesAreRefused()
    {
        var harness = new Harness(TradingStage.Live);

        var zero = await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, 0m, null);
        var negative = await harness.Service.SubmitAsync(UserId, harness.AccountId, Symbol, OrderSide.Buy, -1m, null);

        Assert.Equal(LiveTradeOutcome.Invalid, zero.Outcome);
        Assert.Equal(LiveTradeOutcome.Invalid, negative.Outcome);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AQuantityThatBreaksTheKrakenStepIsRefusedWithoutRounding()
    {
        var harness = new Harness(TradingStage.Live);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.00105m, "live-step");

        Assert.Equal(LiveTradeOutcome.Invalid, result.Outcome);
        Assert.Contains("step", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    [Fact]
    public async Task AReferencePriceThatBreaksTheKrakenTickIsRefusedWithoutRounding()
    {
        var harness = new Harness(
            TradingStage.Live,
            candles: [ClosedCandle(Now.AddMinutes(-1), 30000.05m)]);

        var result = await harness.Service.SubmitAsync(
            UserId, harness.AccountId, Symbol, OrderSide.Buy, 0.001m, "live-tick");

        Assert.Equal(LiveTradeOutcome.Invalid, result.Outcome);
        Assert.Contains("tick", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Adapter.Calls);
    }

    private sealed class Harness
    {
        public Harness(
            TradingStage stage,
            bool accountConnected = true,
            IReadOnlyList<Candle>? candles = null)
        {
            Orders = new InMemoryOrderRepository();
            Positions = new InMemoryPositionRepository();
            Reconciliations = new InMemoryOrderReconciliationRepository();
            Halts = new InMemoryTradingHaltState();
            Audit = new RecordingAudit();
            Adapter = new StubAdapter();
            Options = new LiveTradingOptions { AllowedUserIds = [UserId] };
            Pairs = new StubPairs();

            var time = new FixedTime(Now);

            Account = new ExchangeAccount(
                Guid.NewGuid(),
                UserId,
                ExchangeKind.Kraken,
                "Kraken",
                "exchange-credential/test",
                Now);

            if (accountConnected)
            {
                Account.MarkConnected(Now);
            }

            var current = TradingStage.Paper;
            while (current != stage && accountConnected)
            {
                current++;
                Account.Promote(current, Now);
            }

            Accounts = new StubAccounts(Account);

            Service = new LiveTradingService(
                new StubCandles(candles ??
                [
                    ClosedCandle(Now.AddMinutes(-2), 29900m),
                    ClosedCandle(Now.AddMinutes(-1), 30000m),
                ]),
                Pairs,
                Orders,
                Accounts,
                Adapter,
                new OrderReconciliationService(
                    Orders,
                    Reconciliations,
                    new UnavailableStatusQuery(),
                    Audit,
                    time),
                Halts,
                Audit,
                new RiskEngine(),
                Options,
                time);
        }

        public ExchangeAccount Account { get; }

        public Guid AccountId => Account.Id;

        public InMemoryOrderRepository Orders { get; }

        public InMemoryPositionRepository Positions { get; }

        public InMemoryOrderReconciliationRepository Reconciliations { get; }

        public InMemoryTradingHaltState Halts { get; }

        public RecordingAudit Audit { get; }

        public StubAdapter Adapter { get; }

        public StubAccounts Accounts { get; }

        public LiveTradingOptions Options { get; }

        public StubPairs Pairs { get; }

        public LiveTradingService Service { get; }
    }

    private sealed class StubAdapter : IExecutionAdapter
    {
        public ExecutionOutcome Result { get; set; } = ExecutionOutcome.Accepted;

        public int Calls { get; private set; }

        public Func<Task>? OnExecute { get; set; }

        public async Task<ExecutionResult> ExecuteAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Calls++;

            if (OnExecute is not null)
            {
                await OnExecute().ConfigureAwait(false);
            }

            return new ExecutionResult(
                command.Id,
                success: Result == ExecutionOutcome.Accepted,
                status: Result.ToString(),
                filledQuantity: 0m,
                averageFillPrice: 0m,
                fees: 0m,
                executedAtUtc: Now,
                failureReason: Result == ExecutionOutcome.Accepted ? null : "stubbed",
                outcome: Result);
        }
    }

    private sealed class StubAccounts : IExchangeAccountRepository
    {
        private readonly ExchangeAccount _account;

        public StubAccounts(ExchangeAccount account) => _account = account;

        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExchangeAccount?>(_account.Id == id ? _account : null);

        public Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ExchangeAccount>>(
                _account.UserId == userId ? new[] { _account } : Array.Empty<ExchangeAccount>());

        public Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class UnavailableStatusQuery : Trading.Exchanges.Abstractions.Execution.IExchangeOrderStatusQuery
    {
        public Task<Trading.Exchanges.Abstractions.Execution.OrderStatusQueryResult> QueryByClientOrderIdAsync(
            string clientOrderId,
            string symbol,
            CancellationToken cancellationToken) =>
            Task.FromResult(Trading.Exchanges.Abstractions.Execution.OrderStatusQueryResult.Unavailable(
                "No exchange is reachable in this test."));
    }

    private sealed class StubCandles : IHistoricalCandleSource
    {
        private readonly IReadOnlyList<Candle> _candles;

        public StubCandles(IReadOnlyList<Candle> candles) => _candles = candles;

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_candles);
    }

    private sealed class StubPairs : ITradablePairSource
    {
        public IReadOnlyList<TradablePair> Values { get; set; } =
        [
            new TradablePair(
                Symbol,
                "XBT/USD",
                "XBT",
                "USD",
                isActive: true,
                minimumQuantity: 0.0001m,
                quantityStep: 0.0001m,
                priceTick: 0.1m)
        ];

        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Values);
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

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
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
}
