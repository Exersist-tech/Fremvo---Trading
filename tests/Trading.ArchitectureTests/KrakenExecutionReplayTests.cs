using System.Net;
using Trading.Application.Execution;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
using Trading.Exchanges.Kraken.Execution;
using Trading.Risk;

namespace Trading.ArchitectureTests;

/// <summary>
/// Runs the real Kraken connector, the real execution adapter and the real
/// reconciliation service against recorded exchange answers.
/// </summary>
/// <remarks>
/// <para>
/// These are the tests that stand in for a venue sandbox. Kraken publishes no
/// public Spot test environment, so the execution route cannot be rehearsed at
/// the exchange before real money is involved. Rehearsing it here against
/// recorded answers is what makes the first real order a repeat of something
/// already seen rather than a first attempt.
/// </para>
/// <para>
/// Every client in this file points at the reserved <c>.invalid</c> top-level
/// domain, which by definition never resolves. If the replay handler were ever
/// bypassed, the request would fail to connect rather than reach Kraken.
/// </para>
/// </remarks>
public sealed class KrakenExecutionReplayTests
{
    private const string ReplayBaseAddress = "https://kraken.invalid";
    private const string ClientOrderId = "a4fb4121-bc52-4f70-97fd-7ff27aac9ad3";
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2024, 5, 1, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- harness

    [Fact]
    public async Task TheReplayHandlerRefusesAPathNoTestScripted()
    {
        using var handler = new KrakenReplayHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri(ReplayBaseAddress) };

        using var content = new StringContent("x");

        // Without this, a connector that called an unexpected endpoint would
        // quietly get nothing back and the test would still pass.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsync(new Uri("/0/private/AddOrder", UriKind.Relative), content));
    }

    [Fact]
    public async Task TheReplayHandlerRefusesToRepeatItsLastAnswer()
    {
        using var handler = new KrakenReplayHandler()
            .Script(KrakenResponses.AddOrderPath, ReplayStep.Response(KrakenResponses.OrderAccepted()));
        using var client = new HttpClient(handler) { BaseAddress = new Uri(ReplayBaseAddress) };

        using var first = new StringContent("x");
        await client.PostAsync(new Uri(KrakenResponses.AddOrderPath, UriKind.Relative), first);

        using var second = new StringContent("x");

        // A retry loop that passed by receiving the same recorded answer twice
        // would prove nothing about what the exchange would really do.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsync(new Uri(KrakenResponses.AddOrderPath, UriKind.Relative), second));
    }

    [Fact]
    public void EveryReplayClientTargetsANonResolvableHost()
    {
        // .invalid is reserved by RFC 2606 and can never resolve, so a handler
        // bug cannot turn into a real request.
        Assert.EndsWith(".invalid", new Uri(ReplayBaseAddress).Host, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- connector

    [Fact]
    public async Task AnAcceptedOrderIsReportedAsAccepted()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Response(KrakenResponses.OrderAccepted())));

        var placement = await scope.Gateway.PlaceAsync(Credential(), Request(), CancellationToken.None);

        Assert.Equal(SpotPlacementOutcome.Accepted, placement.Outcome);
        Assert.Equal("OUF4EM-FRGI2-MQMWZD", placement.ExchangeOrderId);
    }

    [Fact]
    public async Task TheOrderSentToKrakenCarriesTheClientOrderIdAndIsNotValidateOnly()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Response(KrakenResponses.OrderAccepted())));

        await scope.Gateway.PlaceAsync(Credential(), Request(), CancellationToken.None);

        var sent = scope.Handler.RequestsTo(KrakenResponses.AddOrderPath).Single();
        Assert.Equal(ClientOrderId, sent.Field("cl_ord_id"));
        Assert.Null(sent.Field("validate"));
    }

    [Fact]
    public async Task AValidateOnlyRequestCarriesKrakensValidateFlag()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Response(KrakenResponses.OrderValidated())));

        var placement = await scope.Gateway.PlaceAsync(
            Credential(), Request(validateOnly: true), CancellationToken.None);

        var sent = scope.Handler.RequestsTo(KrakenResponses.AddOrderPath).Single();
        Assert.Equal("true", sent.Field("validate"));
        Assert.Equal(SpotPlacementOutcome.Validated, placement.Outcome);
    }

    [Fact]
    public async Task AContentRejectionFromKrakenIsADefiniteRefusal()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath,
                ReplayStep.Response(KrakenResponses.Error("EOrder:Invalid volume"))));

        var placement = await scope.Gateway.PlaceAsync(Credential(), Request(), CancellationToken.None);

        Assert.Equal(SpotPlacementOutcome.Rejected, placement.Outcome);
    }

    [Fact]
    public async Task ARateLimitIsNeverReadAsARefusal()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath,
                ReplayStep.Response(KrakenResponses.Error("EAPI:Rate limit exceeded"))));

        var placement = await scope.Gateway.PlaceAsync(Credential(), Request(), CancellationToken.None);

        // Kraken usually does nothing on a rate limit, but "usually" is not
        // proof, and the cost of being wrong is a duplicate position.
        Assert.Equal(SpotPlacementOutcome.Indeterminate, placement.Outcome);
    }

    [Fact]
    public async Task ATimeoutLeavesThePlacementIndeterminate()
    {
        using var scope = Replay((KrakenResponses.AddOrderPath, ReplayStep.Timeout()));

        var placement = await scope.Gateway.PlaceAsync(Credential(), Request(), CancellationToken.None);

        Assert.Equal(SpotPlacementOutcome.Indeterminate, placement.Outcome);
    }

    [Fact]
    public async Task AServerErrorWithNoBodyLeavesThePlacementIndeterminate()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath,
                ReplayStep.Response(string.Empty, HttpStatusCode.InternalServerError)));

        var placement = await scope.Gateway.PlaceAsync(Credential(), Request(), CancellationToken.None);

        Assert.Equal(SpotPlacementOutcome.Indeterminate, placement.Outcome);
    }

    [Fact]
    public async Task KrakenReportingAnOpenOrderIsFound()
    {
        using var scope = Replay(
            (KrakenResponses.QueryOrdersPath,
                ReplayStep.Response(KrakenResponses.OrderFound("OUF4EM-FRGI2-MQMWZD", "open", 0m, 0.25m))));

        var result = await scope.Gateway.QueryAsync(Credential(), ClientOrderId, CancellationToken.None);

        Assert.Equal(OrderStatusQueryOutcome.Found, result.Outcome);
        Assert.Equal(ExchangeOrderState.New, result.State);
    }

    [Fact]
    public async Task KrakenReportingAPartialFillCarriesTheFilledQuantity()
    {
        using var scope = Replay(
            (KrakenResponses.QueryOrdersPath,
                ReplayStep.Response(KrakenResponses.OrderFound("OUF4EM-FRGI2-MQMWZD", "open", 0.1m, 0.25m))));

        var result = await scope.Gateway.QueryAsync(Credential(), ClientOrderId, CancellationToken.None);

        Assert.Equal(0.1m, result.FilledQuantity);
    }

    [Fact]
    public async Task AnEmptyQueryResultIsTheOnlyProofAnOrderIsAbsent()
    {
        using var scope = Replay(
            (KrakenResponses.QueryOrdersPath, ReplayStep.Response(KrakenResponses.NoSuchOrder())));

        var result = await scope.Gateway.QueryAsync(Credential(), ClientOrderId, CancellationToken.None);

        Assert.Equal(OrderStatusQueryOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task AFailedQueryIsUnavailableAndNotAnAbsentOrder()
    {
        using var scope = Replay((KrakenResponses.QueryOrdersPath, ReplayStep.TransportFailure()));

        var result = await scope.Gateway.QueryAsync(Credential(), ClientOrderId, CancellationToken.None);

        Assert.Equal(OrderStatusQueryOutcome.Unavailable, result.Outcome);
        Assert.NotEqual(OrderStatusQueryOutcome.NotFound, result.Outcome);
    }

    // ------------------------------------------------------ adapter over wire

    [Fact]
    public async Task TheAdapterPlacesARealOrderThroughTheConnector()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Response(KrakenResponses.OrderAccepted())));

        var result = await scope.Adapter.ExecuteAsync(LiveCommand(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ExecutionOutcome.Accepted, result.Outcome);
        Assert.Single(scope.Handler.RequestsTo(KrakenResponses.AddOrderPath));
    }

    [Fact]
    public async Task ATimeoutAtTheAdapterProducesAnUnknownOutcome()
    {
        using var scope = Replay((KrakenResponses.AddOrderPath, ReplayStep.Timeout()));

        var result = await scope.Adapter.ExecuteAsync(LiveCommand(), CancellationToken.None);

        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.True(result.RequiresReconciliation);
    }

    [Fact]
    public async Task TheAdapterNeverRetriesAnUnansweredOrderByItself()
    {
        using var scope = Replay((KrakenResponses.AddOrderPath, ReplayStep.Timeout()));

        await scope.Adapter.ExecuteAsync(LiveCommand(), CancellationToken.None);

        // Exactly one attempt. A retry here is the classic duplicate-position
        // bug, and the replay script would have refused a second call anyway.
        Assert.Equal(1, scope.Handler.RequestCount);
    }

    // -------------------------------------------- unanswered order end to end

    [Fact]
    public async Task AnUnansweredOrderThatKrakenNeverReceivedBecomesSafeToResubmit()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Timeout()),
            (KrakenResponses.QueryOrdersPath, ReplayStep.Response(KrakenResponses.NoSuchOrder())));

        var outcome = await RunUnansweredOrderAsync(scope);

        Assert.Equal(ReconciliationDisposition.SafeToResubmit, outcome.Disposition);
        Assert.True(outcome.MayResubmit);
    }

    [Fact]
    public async Task AnUnansweredOrderThatIsActuallyLiveIsNeverResubmitted()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Timeout()),
            (KrakenResponses.QueryOrdersPath,
                ReplayStep.Response(KrakenResponses.OrderFound("OUF4EM-FRGI2-MQMWZD", "open", 0m, 0.25m))));

        var outcome = await RunUnansweredOrderAsync(scope);

        // This is the scenario the whole execution design exists to survive.
        Assert.Equal(ReconciliationDisposition.AdoptedExchangeState, outcome.Disposition);
        Assert.False(outcome.MayResubmit);
    }

    [Fact]
    public async Task AnUnansweredOrderThatAlreadyFilledAdoptsTheFill()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Timeout()),
            (KrakenResponses.QueryOrdersPath,
                ReplayStep.Response(KrakenResponses.OrderFound("OUF4EM-FRGI2-MQMWZD", "closed", 0.25m, 0.25m))));

        var outcome = await RunUnansweredOrderAsync(scope);

        Assert.False(outcome.MayResubmit);
        Assert.Equal(ExchangeOrderStatus.Filled, outcome.ObservedStatus);
    }

    [Fact]
    public async Task AnUnansweredOrderStaysFrozenWhenKrakenCannotBeReached()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Timeout()),
            (KrakenResponses.QueryOrdersPath, ReplayStep.TransportFailure()));

        var outcome = await RunUnansweredOrderAsync(scope);

        // Failing to learn the answer is not an answer.
        Assert.Equal(ReconciliationDisposition.Unresolved, outcome.Disposition);
        Assert.False(outcome.MayResubmit);
    }

    [Fact]
    public async Task ReconciliationQueriesByTheClientOrderIdNotTheExchangeId()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Timeout()),
            (KrakenResponses.QueryOrdersPath, ReplayStep.Response(KrakenResponses.NoSuchOrder())));

        await RunUnansweredOrderAsync(scope);

        // The exchange id is exactly what is missing after a timeout, so the
        // lookup has to use the identifier the platform always knows.
        var query = scope.Handler.RequestsTo(KrakenResponses.QueryOrdersPath).Single();
        Assert.NotNull(query.Field("cl_ord_id"));
    }

    [Fact]
    public async Task ReconciliationNeverSendsAnOrderOfItsOwn()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Timeout()),
            (KrakenResponses.QueryOrdersPath, ReplayStep.Response(KrakenResponses.NoSuchOrder())));

        await RunUnansweredOrderAsync(scope);

        // One AddOrder attempt, and it was the adapter's. Reconciliation only
        // reads.
        Assert.Single(scope.Handler.RequestsTo(KrakenResponses.AddOrderPath));
    }

    [Fact]
    public async Task AStatusQueryForAnUnresolvableAccountIsUnavailableRatherThanNotFound()
    {
        using var handler = new KrakenReplayHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(ReplayBaseAddress) };
        var gateway = new KrakenSpotOrderGateway(http, new FixedTime(Now));
        var query = new SpotOrderStatusQuery(gateway, new NullAccountSource(), AccountId);

        var result = await query.QueryByClientOrderIdAsync(ClientOrderId, "XBTUSD", CancellationToken.None);

        Assert.Equal(OrderStatusQueryOutcome.Unavailable, result.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    // ----------------------------------------------------------------- guards

    [Fact]
    public async Task NoTestInThisSuiteCanSubmitARealOrder()
    {
        using var scope = Replay(
            (KrakenResponses.AddOrderPath, ReplayStep.Response(KrakenResponses.OrderAccepted())));

        await scope.Adapter.ExecuteAsync(LiveCommand(), CancellationToken.None);

        // Two independent reasons a real order was impossible: the request was
        // answered by the replay handler, and the only host configured cannot
        // resolve.
        Assert.All(
            scope.Handler.Requests,
            request => Assert.StartsWith("/0/private/", request.Path, StringComparison.Ordinal));
        Assert.EndsWith(".invalid", new Uri(ReplayBaseAddress).Host, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<ReconciliationOutcome> RunUnansweredOrderAsync(ReplayScope scope)
    {
        var placement = await scope.Adapter.ExecuteAsync(LiveCommand(), CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Unknown, placement.Outcome);

        var order = new Order(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "XBTUSD",
            OrderSide.Buy,
            OrderType.Limit,
            0.25m,
            50_000m,
            Now,
            ClientOrderId);

        await scope.Orders.AddAsync(order, CancellationToken.None);

        var record = await scope.Reconciliation.OpenAsync(
            order,
            "kraken-spot-adapter",
            placement.FailureReason ?? "The submission was not answered.",
            CancellationToken.None);

        return await scope.Reconciliation.ResolveAsync(record, CancellationToken.None);
    }

    private static ExchangeCredential Credential() => new("test-key", "dGVzdC1zZWNyZXQ=");

    private static SpotOrderRequest Request(bool validateOnly = false) =>
        new("XBTUSD", SpotOrderSide.Buy, SpotOrderType.Limit, 0.25m, 50_000m, ClientOrderId, validateOnly);

    private static ExecutionCommand LiveCommand() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "XBTUSD",
            TradeDirection.Buy,
            0.25m,
            50_000m,
            Now,
            ClientOrderId,
            isPaperOnly: false,
            exchangeAccountId: AccountId);

    private static ReplayScope Replay(params (string Path, ReplayStep Step)[] script)
    {
        var handler = new KrakenReplayHandler();
        foreach (var (path, step) in script)
        {
            handler.Script(path, step);
        }

        return new ReplayScope(handler, ReplayBaseAddress, AccountId, Now);
    }

    private sealed class ReplayScope : IDisposable
    {
        private readonly HttpClient _http;

        public ReplayScope(KrakenReplayHandler handler, string baseAddress, Guid accountId, DateTimeOffset now)
        {
            Handler = handler;
            _http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };

            var time = new FixedTime(now);
            Gateway = new KrakenSpotOrderGateway(_http, time);

            var accounts = new SingleAccountSource(new SpotExecutionAccount(
                accountId,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                ExchangeKind.Kraken,
                TradingStage.Proving,
                new ExchangeCredential("test-key", "dGVzdC1zZWNyZXQ=")));

            Adapter = new SpotExecutionAdapter(Gateway, accounts, time);
            Reconciliation = new OrderReconciliationService(
                Orders,
                new InMemoryOrderReconciliationRepository(),
                new SpotOrderStatusQuery(Gateway, accounts, accountId),
                new SilentAudit(),
                time);
        }

        public KrakenReplayHandler Handler { get; }

        public KrakenSpotOrderGateway Gateway { get; }

        public SpotExecutionAdapter Adapter { get; }

        public InMemoryOrderRepository Orders { get; } = new();

        public OrderReconciliationService Reconciliation { get; }

        public void Dispose()
        {
            _http.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class SingleAccountSource : ISpotExecutionAccountSource
    {
        private readonly SpotExecutionAccount _account;

        public SingleAccountSource(SpotExecutionAccount account) => _account = account;

        public Task<SpotExecutionAccount?> ResolveAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<SpotExecutionAccount?>(_account);
    }

    private sealed class NullAccountSource : ISpotExecutionAccountSource
    {
        public Task<SpotExecutionAccount?> ResolveAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<SpotExecutionAccount?>(null);
    }

    private sealed class SilentAudit : IAuditEventWriter
    {
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

/// <summary>
/// The restrictions that bound a proving account's real orders.
/// </summary>
public sealed class ProvingRestrictionTests
{
    private static readonly string[] Approved = ["XBTUSD", "ETHUSD"];

    private static RiskEvaluationResult Evaluate(ProvingRestriction restriction) =>
        new RiskEngine().Evaluate(
            proposedExposure: 100m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 1_000_000m,
            maxNotional: 1_000_000m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false,
            provingRestriction: restriction);

    [Fact]
    public void AnOrderWithinTheProvingCeilingIsAllowed() =>
        Assert.True(Evaluate(new ProvingRestriction("XBTUSD", 25m, 100m, Approved)).IsAllowed);

    [Fact]
    public void AnOrderAboveTheProvingCeilingIsBlocked() =>
        Assert.False(Evaluate(new ProvingRestriction("XBTUSD", 250m, 100m, Approved)).IsAllowed);

    [Fact]
    public void AnOrderExactlyAtTheCeilingIsAllowed() =>
        Assert.True(Evaluate(new ProvingRestriction("XBTUSD", 100m, 100m, Approved)).IsAllowed);

    [Fact]
    public void AnUnapprovedInstrumentIsBlockedDuringProving() =>
        Assert.False(Evaluate(new ProvingRestriction("DOGEUSD", 5m, 100m, Approved)).IsAllowed);

    [Fact]
    public void AMissingCeilingBlocksEveryProvingOrder()
    {
        // "Not configured" must read as "nothing approved", never as
        // "no limit".
        var result = Evaluate(new ProvingRestriction("XBTUSD", 1m, null, Approved));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void AnEmptyApprovedInstrumentSetBlocksEveryProvingOrder() =>
        Assert.False(Evaluate(new ProvingRestriction("XBTUSD", 1m, 100m, [])).IsAllowed);

    [Fact]
    public void TheCeilingIsEnforcedByTheRiskEngineRatherThanTheAdapter()
    {
        // The adapter is the only component that can reach the exchange. If it
        // were also the only component enforcing the ceiling, a bug there would
        // have nothing behind it.
        var blocked = Evaluate(new ProvingRestriction("XBTUSD", 5_000m, 100m, Approved));

        Assert.False(blocked.IsAllowed);
        Assert.NotEmpty(blocked.ActiveLimits);
    }

    [Fact]
    public void AHaltIsReportedBeforeAProvingViolation()
    {
        var result = new RiskEngine().Evaluate(
            100m, 0m, 0m, 0, 0, 1_000_000m, 1_000_000m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: true,
            provingRestriction: new ProvingRestriction("DOGEUSD", 9_999m, 1m, Approved));

        Assert.False(result.IsAllowed);
        Assert.Contains("halted", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaleDataBlocksAProvingOrderEvenWithinTheCeiling()
    {
        var result = new RiskEngine().Evaluate(
            100m, 0m, 0m, 0, 0, 1_000_000m, 1_000_000m,
            dataIsStale: true,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false,
            provingRestriction: new ProvingRestriction("XBTUSD", 1m, 100m, Approved));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void AnAccountStartsWithNoProvingCeiling()
    {
        var account = new ExchangeAccount(
            Guid.NewGuid(), Guid.NewGuid(), ExchangeKind.Kraken, "Main", "secrets/abc", DateTimeOffset.UtcNow);

        Assert.Null(account.ProvingNotionalCeiling);
    }

    [Fact]
    public void AProvingCeilingMustBePositive()
    {
        var account = new ExchangeAccount(
            Guid.NewGuid(), Guid.NewGuid(), ExchangeKind.Kraken, "Main", "secrets/abc", DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetProvingNotionalCeiling(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => account.SetProvingNotionalCeiling(-1m));
    }

    [Fact]
    public void ClearingTheCeilingStopsProvingOrders()
    {
        var account = new ExchangeAccount(
            Guid.NewGuid(), Guid.NewGuid(), ExchangeKind.Kraken, "Main", "secrets/abc", DateTimeOffset.UtcNow);

        account.SetProvingNotionalCeiling(100m);
        account.ClearProvingNotionalCeiling();

        Assert.Null(account.ProvingNotionalCeiling);
        Assert.False(Evaluate(new ProvingRestriction("XBTUSD", 1m, account.ProvingNotionalCeiling, Approved)).IsAllowed);
    }
}
