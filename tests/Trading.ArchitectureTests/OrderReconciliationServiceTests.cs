using Trading.Application.Execution;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.ArchitectureTests;

public sealed class OrderReconciliationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OpeningAReconciliationFreezesTheOrder()
    {
        var harness = new Harness();
        var order = harness.AddOrder();

        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        Assert.True(order.RequiresReconciliation);
        Assert.False(order.CanResubmit);
        Assert.Equal(ExchangeOrderStatus.Unknown, record.ObservedStatus);
        Assert.False(record.IsResolved);
        Assert.True(record.RequiresResolutionBeforeResubmission);
        Assert.Contains(harness.AuditActions, action => action == "Trade.ReconciliationOpened");
    }

    [Fact]
    public async Task AFailedQueryLeavesTheOrderFrozen()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.Unavailable("The exchange did not respond.");

        var outcome = await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Unresolved, outcome.Disposition);
        Assert.False(outcome.MayResubmit);
        Assert.True(order.RequiresReconciliation);
        Assert.False(record.IsResolved);
    }

    [Fact]
    public async Task AThrowingConnectorIsTreatedAsNothingProven()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Throw = true;

        var outcome = await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.Unresolved, outcome.Disposition);
        Assert.False(outcome.MayResubmit);
        Assert.True(order.RequiresReconciliation);
    }

    [Fact]
    public async Task AnAbsentOrderIsProvenSafeToResubmit()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.NotFound();

        var outcome = await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.SafeToResubmit, outcome.Disposition);
        Assert.True(outcome.MayResubmit);
        Assert.False(order.RequiresReconciliation);
        Assert.Equal(OrderState.Rejected, order.State);
        Assert.True(record.IsResolved);
    }

    [Fact]
    public async Task AFilledOrderIsAdoptedAndNeverResubmitted()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.Found(ExchangeOrderState.Filled, "EX-1", 2m);

        var outcome = await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.AdoptedExchangeState, outcome.Disposition);
        Assert.False(outcome.MayResubmit);
        Assert.Equal(OrderState.Filled, order.State);
        Assert.Equal(2m, order.FilledQuantity);
        Assert.Equal("EX-1", order.ExchangeOrderId);
        Assert.False(order.CanResubmit);
    }

    [Fact]
    public async Task ALiveOrderIsAdoptedAndNeverResubmitted()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.Found(ExchangeOrderState.New, "EX-2", 0m);

        var outcome = await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.AdoptedExchangeState, outcome.Disposition);
        Assert.False(outcome.MayResubmit);
        Assert.Equal(OrderState.New, order.State);
    }

    [Fact]
    public async Task APartiallyFilledOrderIsAdoptedWithItsFills()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.Found(ExchangeOrderState.PartiallyFilled, "EX-3", 0.5m);

        var outcome = await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.AdoptedExchangeState, outcome.Disposition);
        Assert.Equal(OrderState.PartiallyFilled, order.State);
        Assert.Equal(0.5m, order.FilledQuantity);
        Assert.Equal(1.5m, order.RemainingQuantity);
    }

    [Fact]
    public async Task APendingCancelKeepsTheOrderLive()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.Found(ExchangeOrderState.PendingCancel, "EX-4", 0m);

        var outcome = await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Equal(ReconciliationDisposition.AdoptedExchangeState, outcome.Disposition);
        Assert.False(outcome.MayResubmit);
        Assert.Equal(OrderState.New, order.State);
    }

    [Fact]
    public async Task EveryResolutionIsAudited()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.NotFound();
        await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Contains("Trade.ReconciliationOpened", harness.AuditActions);
        Assert.Contains("Trade.ReconciliationResolved", harness.AuditActions);
    }

    [Fact]
    public async Task AFailedQueryIsAudited()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        var record = await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        harness.Query.Result = OrderStatusQueryResult.Unavailable("No response.");
        await harness.Service.ResolveAsync(record, CancellationToken.None);

        Assert.Contains("Trade.ReconciliationUnavailable", harness.AuditActions);
    }

    [Fact]
    public async Task UnresolvedRecordsRemainVisible()
    {
        var harness = new Harness();
        var order = harness.AddOrder();
        await harness.Service.OpenAsync(
            order, "spot-adapter", "The submission timed out.", CancellationToken.None);

        var unresolved = await harness.Records.ListUnresolvedAsync(CancellationToken.None);

        Assert.Single(unresolved);
    }

    [Fact]
    public void AFoundResultCannotReportAnUnknownState() =>
        Assert.Throws<ArgumentException>(
            () => OrderStatusQueryResult.Found(ExchangeOrderState.Unknown, "EX", 0m));

    [Fact]
    public void AnUnavailableResultProvesNothing()
    {
        var result = OrderStatusQueryResult.Unavailable("Timeout.");

        Assert.Equal(OrderStatusQueryOutcome.Unavailable, result.Outcome);
        Assert.Equal(ExchangeOrderState.Unknown, result.State);
    }

    [Fact]
    public void NotFoundAndUnavailableAreDistinct() =>
        Assert.NotEqual(
            OrderStatusQueryResult.NotFound().Outcome,
            OrderStatusQueryResult.Unavailable("Timeout.").Outcome);

    private sealed class Harness
    {
        public Harness()
        {
            var time = new FakeTimeProvider(Now);
            Service = new OrderReconciliationService(Orders, Records, Query, Audit, time);
        }

        public InMemoryOrderRepository Orders { get; } = new();

        public InMemoryOrderReconciliationRepository Records { get; } = new();

        public StubStatusQuery Query { get; } = new();

        public RecordingAuditWriter Audit { get; } = new();

        public OrderReconciliationService Service { get; }

        public IReadOnlyList<string> AuditActions => Audit.Actions;

        public Order AddOrder()
        {
            var order = new Order(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "BTCUSDT",
                OrderSide.Buy,
                OrderType.Limit,
                2m,
                50000m,
                Now,
                $"coid-{Guid.NewGuid():N}");

            Orders.AddAsync(order, CancellationToken.None).GetAwaiter().GetResult();
            return order;
        }
    }

    private sealed class StubStatusQuery : IExchangeOrderStatusQuery
    {
        public OrderStatusQueryResult Result { get; set; } =
            OrderStatusQueryResult.Unavailable("Not configured.");

        public bool Throw { get; set; }

        public Task<OrderStatusQueryResult> QueryByClientOrderIdAsync(
            string clientOrderId,
            string symbol,
            CancellationToken cancellationToken) =>
            Throw
                ? throw new InvalidOperationException("Connector failure.")
                : Task.FromResult(Result);
    }

    private sealed class RecordingAuditWriter : IAuditEventWriter
    {
        private readonly List<string> _actions = [];

        public IReadOnlyList<string> Actions => _actions;

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(auditEvent);
            _actions.Add(auditEvent.Action);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

public sealed class OrderStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static Order NewOrder() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "BTCUSDT",
        OrderSide.Buy,
        OrderType.Limit,
        2m,
        50000m,
        Now,
        "coid-1");

    [Fact]
    public void ANewOrderMayBeSubmitted()
    {
        var order = NewOrder();

        Assert.True(order.CanResubmit);
        Assert.False(order.IsTerminal);
    }

    [Fact]
    public void SubmissionRecordsTheExchangeIdentifier()
    {
        var order = NewOrder();
        order.MarkSubmitted("EX-9", Now);

        Assert.Equal("EX-9", order.ExchangeOrderId);
        Assert.Equal(OrderState.New, order.State);
        Assert.Equal(Now, order.LastTransitionAtUtc);
    }

    [Fact]
    public void FillsAreCumulativeAndCannotDecrease()
    {
        var order = NewOrder();
        order.MarkPartiallyFilled(1m, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => order.MarkPartiallyFilled(0.5m, Now));
    }

    [Fact]
    public void FillsCannotExceedTheOrderedQuantity()
    {
        var order = NewOrder();

        Assert.Throws<ArgumentOutOfRangeException>(() => order.MarkPartiallyFilled(2.5m, Now));
    }

    [Fact]
    public void AFullFillClosesTheOrder()
    {
        var order = NewOrder();
        order.MarkPartiallyFilled(2m, Now);

        Assert.Equal(OrderState.Filled, order.State);
        Assert.True(order.IsTerminal);
        Assert.Equal(0m, order.RemainingQuantity);
    }

    [Fact]
    public void ATerminalOrderCannotBeSubmittedAgain()
    {
        var order = NewOrder();
        order.MarkPartiallyFilled(2m, Now);

        Assert.Throws<InvalidOperationException>(() => order.MarkSubmitted("EX-1", Now));
    }

    [Fact]
    public void AnUnknownOrderCannotBeResubmitted()
    {
        var order = NewOrder();
        order.MarkUnknown("Timed out.", Now);

        Assert.False(order.CanResubmit);
        Assert.True(order.RequiresReconciliation);
    }

    [Fact]
    public void ResolutionRequiresAnOpenReconciliation()
    {
        var order = NewOrder();

        Assert.Throws<InvalidOperationException>(
            () => order.ResolveReconciliation(OrderState.Rejected, 0m, null, "reason", Now));
    }

    [Fact]
    public void ResolutionMustAssertWhatHappened()
    {
        var order = NewOrder();
        order.MarkUnknown("Timed out.", Now);

        Assert.Throws<ArgumentException>(
            () => order.ResolveReconciliation(OrderState.Draft, 0m, null, "reason", Now));
    }

    [Fact]
    public void ResolutionCannotLoseRecordedFills()
    {
        var order = NewOrder();
        order.MarkPartiallyFilled(1m, Now);
        order.MarkUnknown("Timed out.", Now);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => order.ResolveReconciliation(OrderState.New, 0.5m, null, "reason", Now));
    }

    [Fact]
    public void EveryTransitionAdvancesTheConcurrencyVersion()
    {
        var order = NewOrder();
        var initial = order.Version;

        order.MarkSubmitted("EX-1", Now);
        order.MarkPartiallyFilled(1m, Now);

        Assert.Equal(initial + 2, order.Version);
    }

    [Fact]
    public async Task ClientOrderIdReuseIsRejected()
    {
        var repository = new InMemoryOrderRepository();
        var first = NewOrder();
        await repository.AddAsync(first, CancellationToken.None);

        var duplicate = new Order(
            Guid.NewGuid(),
            first.UserId,
            first.StrategyId,
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Limit,
            2m,
            50000m,
            Now,
            first.ClientOrderId);

        await Assert.ThrowsAsync<DuplicateClientOrderIdException>(
            () => repository.AddAsync(duplicate, CancellationToken.None));
    }

    [Fact]
    public async Task OrdersAreIsolatedBetweenUsers()
    {
        var repository = new InMemoryOrderRepository();
        var order = NewOrder();
        await repository.AddAsync(order, CancellationToken.None);

        var otherUser = await repository.GetAsync(Guid.NewGuid(), order.Id, CancellationToken.None);
        var owner = await repository.GetAsync(order.UserId, order.Id, CancellationToken.None);

        Assert.Null(otherUser);
        Assert.NotNull(owner);
    }

    [Fact]
    public async Task ListingIsScopedToTheOwningUser()
    {
        var repository = new InMemoryOrderRepository();
        var order = NewOrder();
        await repository.AddAsync(order, CancellationToken.None);

        var others = await repository.ListAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Empty(others);
    }
}

public sealed class PositionStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static Position NewPosition() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "BTCUSDT",
        PositionDirection.DirectionLong,
        1m,
        50000m,
        50000m,
        Now);

    [Fact]
    public void AnOpenPositionPermitsIncrease()
    {
        var position = NewPosition();

        Assert.True(position.PermitsIncrease);
    }

    [Fact]
    public void AReduceOnlyPositionBlocksIncrease()
    {
        var position = NewPosition();
        position.RestrictToReduceOnly(Now);

        Assert.False(position.PermitsIncrease);
        Assert.Equal(PositionStatus.ReducedOnly, position.Status);
    }

    [Fact]
    public void AClosingPositionBlocksIncrease()
    {
        var position = NewPosition();
        position.BeginClosing(Now);

        Assert.False(position.PermitsIncrease);
        Assert.Equal(PositionStatus.Closing, position.Status);
    }

    [Fact]
    public void ALiquidatedPositionCannotBeRestricted()
    {
        var position = NewPosition();
        position.Liquidate();

        Assert.Throws<InvalidOperationException>(() => position.RestrictToReduceOnly(Now));
    }

    [Fact]
    public void AFlatPositionCannotBeginClosing()
    {
        var position = NewPosition();
        position.Reduce(1m);

        Assert.Throws<InvalidOperationException>(() => position.BeginClosing(Now));
    }

    [Fact]
    public void RestrictingAdvancesTheConcurrencyVersion()
    {
        var position = NewPosition();
        var initial = position.Version;

        position.RestrictToReduceOnly(Now);

        Assert.Equal(initial + 1, position.Version);
    }

    [Fact]
    public async Task PositionsAreIsolatedBetweenUsers()
    {
        var repository = new InMemoryPositionRepository();
        var position = NewPosition();
        await repository.AddAsync(position, CancellationToken.None);

        Assert.Null(await repository.GetAsync(Guid.NewGuid(), position.Id, CancellationToken.None));
        Assert.Empty(await repository.ListOpenAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
