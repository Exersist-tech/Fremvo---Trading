using Trading.Application.Execution;
using Trading.Domain.Execution;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.ArchitectureTests;

/// <summary>
/// Tests for the adapter that turns a pipeline execution command into a real
/// spot order.
/// </summary>
/// <remarks>
/// The behaviour these tests protect is not "orders get placed". It is that
/// the four conditions guarding a real order refuse rather than adapt, and
/// that an unestablished outcome is never reported as a refusal.
/// </remarks>
public sealed class SpotExecutionAdapterTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static ExecutionCommand Command(bool paperOnly = false, Guid? accountId = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "XBTUSD",
            TradeDirection.Buy,
            0.25m,
            50_000m,
            Now,
            "fremvo-abc123",
            isPaperOnly: paperOnly,
            exchangeAccountId: paperOnly ? null : accountId ?? AccountId);

    private static SpotExecutionAccount Account(
        TradingStage stage = TradingStage.Proving,
        ExchangeKind exchange = ExchangeKind.Kraken) =>
        new(AccountId, UserId, exchange, stage, new ExchangeCredential("key", "c2VjcmV0"));

    private static SpotExecutionAdapter Adapter(
        StubGateway gateway,
        SpotExecutionAccount? account)
        => new(gateway, new StubAccountSource(account), new FixedTimeProvider(Now));

    [Fact]
    public async Task APaperOnlyCommandIsNeverSentToTheExchange()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command(paperOnly: true));

        Assert.False(result.Success);
        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(0, gateway.PlaceCalls);
    }

    [Fact]
    public async Task APaperOnlyCommandIsRefusedRatherThanSimulated()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command(paperOnly: true));

        // A silent fallback to simulation would report a fill the user does
        // not actually hold anywhere.
        Assert.Equal("Refused", result.Status);
        Assert.Equal(0m, result.FilledQuantity);
    }

    [Fact]
    public async Task AnUnresolvableAccountStopsTheOrder()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        var result = await Adapter(gateway, account: null).ExecuteAsync(Command());

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(0, gateway.PlaceCalls);
    }

    [Fact]
    public async Task AnAccountStillInPaperStageCannotSendOrders()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        var result = await Adapter(gateway, Account(TradingStage.Paper)).ExecuteAsync(Command());

        Assert.False(result.Success);
        Assert.Equal(0, gateway.PlaceCalls);
        Assert.Contains("paper stage", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAccountOnADifferentExchangeCannotUseThisRoute()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        var result = await Adapter(gateway, Account(exchange: ExchangeKind.None))
            .ExecuteAsync(Command());

        Assert.False(result.Success);
        Assert.Equal(0, gateway.PlaceCalls);
    }

    [Fact]
    public async Task AProvingAccountMayPlaceAnOrder()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        var result = await Adapter(gateway, Account(TradingStage.Proving)).ExecuteAsync(Command());

        Assert.True(result.Success);
        Assert.Equal(1, gateway.PlaceCalls);
    }

    [Fact]
    public async Task TheRequestSentToTheExchangeAsksForARealOrder()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        await Adapter(gateway, Account()).ExecuteAsync(Command());

        Assert.NotNull(gateway.LastRequest);
        Assert.False(gateway.LastRequest!.ValidateOnly);
    }

    [Fact]
    public async Task ThePlatformClientOrderIdTravelsWithTheOrder()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        await Adapter(gateway, Account()).ExecuteAsync(Command());

        // Without this the order is unfindable when the response is lost,
        // and the exchange cannot reject a duplicate submission.
        Assert.Equal("fremvo-abc123", gateway.LastRequest!.ClientOrderId);
    }

    [Fact]
    public async Task TheCommandDirectionAndSizeAreCarriedUnchanged()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));
        var command = Command();

        await Adapter(gateway, Account()).ExecuteAsync(command);

        Assert.Equal(SpotOrderSide.Buy, gateway.LastRequest!.Side);
        Assert.Equal(command.Quantity, gateway.LastRequest.Quantity);
        Assert.Equal(command.Price, gateway.LastRequest.LimitPrice);
        Assert.Equal(command.Symbol, gateway.LastRequest.Symbol);
    }

    [Fact]
    public async Task ASellCommandBecomesASellOrder()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));
        var command = new ExecutionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "XBTUSD",
            TradeDirection.Sell,
            0.25m,
            50_000m,
            Now,
            "fremvo-sell1",
            isPaperOnly: false,
            exchangeAccountId: AccountId);

        await Adapter(gateway, Account()).ExecuteAsync(command);

        Assert.Equal(SpotOrderSide.Sell, gateway.LastRequest!.Side);
    }

    [Fact]
    public async Task AcceptanceIsNotReportedAsAFill()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Accepted("fremvo-abc123", "TX-1"));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command());

        // An accepted limit order has not traded. Reporting the limit price as
        // an average fill price would invent a position.
        Assert.Equal(0m, result.FilledQuantity);
        Assert.Equal(0m, result.AverageFillPrice);
        Assert.Equal(0m, result.Fees);
    }

    [Fact]
    public async Task AnExchangeRejectionIsReportedAsRejected()
    {
        var gateway = new StubGateway(
            SpotOrderPlacement.Rejected("fremvo-abc123", ["EOrder:Invalid volume"]));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command());

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.False(result.RequiresReconciliation);
        Assert.Contains("Invalid volume", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIndeterminatePlacementIsNeverReportedAsRejected()
    {
        var gateway = new StubGateway(
            SpotOrderPlacement.Indeterminate("fremvo-abc123", ["The request timed out."]));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command());

        // This is the distinction the whole execution path exists to keep.
        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.NotEqual(ExecutionOutcome.Rejected, result.Outcome);
        Assert.True(result.RequiresReconciliation);
    }

    [Fact]
    public async Task ADuplicateClientOrderIdIsTreatedAsAnUnknownOrderNotARejection()
    {
        var gateway = new StubGateway(
            SpotOrderPlacement.Duplicate("fremvo-abc123", ["EOrder:Duplicate cl_ord_id"]));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command());

        // The duplicate is evidence an earlier submission reached the venue,
        // so the existing order must be found rather than replaced.
        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.True(result.RequiresReconciliation);
    }

    [Fact]
    public async Task AValidationAnswerToARealOrderIsTreatedAsUnknown()
    {
        var gateway = new StubGateway(SpotOrderPlacement.Validated("fremvo-abc123"));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command());

        // The route did not do what it was told. Nothing about it can be
        // trusted, including the claim that nothing was placed.
        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task AThrowingGatewayProducesAnUnknownOutcomeRatherThanARejection()
    {
        var gateway = new StubGateway(new InvalidOperationException("socket closed"));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command());

        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.True(result.RequiresReconciliation);
    }

    [Fact]
    public async Task CancellationIsNotSwallowedAsAnUnknownOutcome()
    {
        var gateway = new StubGateway(new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Adapter(gateway, Account()).ExecuteAsync(Command()));
    }

    [Fact]
    public async Task TheFailureReasonNeverContainsTheApiSecret()
    {
        var gateway = new StubGateway(
            SpotOrderPlacement.Indeterminate("fremvo-abc123", ["Timed out."]));

        var result = await Adapter(gateway, Account()).ExecuteAsync(Command());

        Assert.DoesNotContain("c2VjcmV0", result.FailureReason ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("key", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandThatMayReachAVenueMustNameAnAccount()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "XBTUSD",
            TradeDirection.Buy,
            1m,
            10m,
            Now,
            "fremvo-x",
            isPaperOnly: false,
            exchangeAccountId: null));
    }

    [Fact]
    public void APaperOnlyCommandNeedsNoAccount()
    {
        var command = new ExecutionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "XBTUSD",
            TradeDirection.Buy,
            1m,
            10m,
            Now,
            "fremvo-x");

        Assert.True(command.IsPaperOnly);
        Assert.Null(command.ExchangeAccountId);
    }

    [Fact]
    public void AnExecutionResultWithoutAStatedOutcomeNeverDefaultsToUnknown()
    {
        // Callers that cannot be in doubt, such as the paper adapter, omit the
        // outcome. Defaulting those to Unknown would send simulated fills to
        // reconciliation forever.
        var success = new ExecutionResult(Guid.NewGuid(), true, "Filled", 1m, 10m, 0m, Now);
        var failure = new ExecutionResult(Guid.NewGuid(), false, "Rejected", 0m, 0m, 0m, Now, "no");

        Assert.Equal(ExecutionOutcome.Filled, success.Outcome);
        Assert.Equal(ExecutionOutcome.Rejected, failure.Outcome);
        Assert.False(success.RequiresReconciliation);
        Assert.False(failure.RequiresReconciliation);
    }

    private sealed class StubGateway : ISpotOrderGateway
    {
        private readonly SpotOrderPlacement? _placement;
        private readonly Exception? _exception;

        public StubGateway(SpotOrderPlacement placement) => _placement = placement;

        public StubGateway(Exception exception) => _exception = exception;

        public ExchangeKind Exchange => ExchangeKind.Kraken;

        public int PlaceCalls { get; private set; }

        public SpotOrderRequest? LastRequest { get; private set; }

        public Task<SpotOrderPlacement> PlaceAsync(
            ExchangeCredential credential,
            SpotOrderRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceCalls++;
            LastRequest = request;

            return _exception is not null
                ? Task.FromException<SpotOrderPlacement>(_exception)
                : Task.FromResult(_placement!);
        }

        public Task<SpotOrderCancellation> CancelAsync(
            ExchangeCredential credential,
            string clientOrderId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OrderStatusQueryResult> QueryAsync(
            ExchangeCredential credential,
            string clientOrderId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SpotFillQueryResult> ListFillsAsync(
            ExchangeCredential credential,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubAccountSource : ISpotExecutionAccountSource
    {
        private readonly SpotExecutionAccount? _account;

        public StubAccountSource(SpotExecutionAccount? account) => _account = account;

        public Task<SpotExecutionAccount?> ResolveAsync(
            Guid exchangeAccountId,
            CancellationToken cancellationToken)
            => Task.FromResult(_account);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
