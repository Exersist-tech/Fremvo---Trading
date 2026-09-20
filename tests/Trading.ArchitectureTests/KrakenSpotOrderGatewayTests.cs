using System.Globalization;
using System.Net;
using System.Text;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
using Trading.Exchanges.Kraken;
using Trading.Exchanges.Kraken.Execution;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers the Kraken Spot order gateway.
/// </summary>
/// <remarks>
/// <para>
/// No test here holds a real credential or reaches a network. Every response
/// is supplied by a stub handler, so the suite cannot place an order.
/// </para>
/// <para>
/// The behaviour these assert most carefully is the separation between "the
/// exchange refused this order" and "we do not know what the exchange did".
/// Collapsing those two answers is how a timeout becomes a duplicate position.
/// </para>
/// </remarks>
public sealed class KrakenSpotOrderGatewayTests
{
    private const string ApiKey = "kraken-public-key-abcdef";
    private const string Secret = "c29tZS1sb25nLXNlY3JldC12YWx1ZQ==";
    private const string ClientOrderId = "6d1b345e-2821-40e2-ad83-4ecb18a06876";

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static ExchangeCredential Credential() => new(ApiKey, Secret);

    private static SpotOrderRequest LimitBuy(bool validateOnly = true) =>
        new("XBTUSD", SpotOrderSide.Buy, SpotOrderType.Limit, 0.25m, 30000.5m, ClientOrderId, validateOnly);

    // ---- Request shape -------------------------------------------------

    [Fact]
    public void AnOrderBodyCarriesTheClientOrderIdSoKrakenEnforcesIdempotency()
    {
        var body = KrakenSpotOrderGateway.BuildAddOrderBody("1", LimitBuy());

        // Kraken rejects a reused cl_ord_id, which makes duplicate protection
        // something the exchange enforces rather than something this code hopes
        // for.
        Assert.Contains("cl_ord_id=" + ClientOrderId, body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrderBodyFormatsNumbersWithTheInvariantCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A machine with a comma decimal separator would otherwise send a
            // quantity Kraken reads as a different number.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("nb-NO");

            var body = KrakenSpotOrderGateway.BuildAddOrderBody("1", LimitBuy());

            Assert.Contains("volume=0.25", body, StringComparison.Ordinal);
            Assert.Contains("price=30000.5", body, StringComparison.Ordinal);
            Assert.DoesNotContain(",", body, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void ARequestValidatesOnlyUnlessTheCallerExplicitlyAsksToPlace()
    {
        // The safe value is the default, because the unsafe value spends money.
        var implicitRequest = new SpotOrderRequest(
            "XBTUSD", SpotOrderSide.Buy, SpotOrderType.Limit, 0.25m, 30000m, ClientOrderId);

        Assert.True(implicitRequest.ValidateOnly);
        Assert.Contains(
            "validate=true",
            KrakenSpotOrderGateway.BuildAddOrderBody("1", implicitRequest),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARealOrderBodyDoesNotCarryTheValidateFlag()
    {
        var body = KrakenSpotOrderGateway.BuildAddOrderBody("1", LimitBuy(validateOnly: false));

        Assert.DoesNotContain("validate", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ALimitOrderWithoutAPriceIsRefusedRatherThanTurnedIntoAMarketOrder()
    {
        // Substituting a price would turn the strategy's instruction into a
        // materially different order.
        Assert.Throws<ArgumentNullException>(() => new SpotOrderRequest(
            "XBTUSD", SpotOrderSide.Buy, SpotOrderType.Limit, 1m, null, ClientOrderId));
    }

    [Fact]
    public void AMarketOrderCarryingAPriceIsRefused()
    {
        // The exchange would ignore it while the code suggests it was honoured.
        Assert.Throws<ArgumentException>(() => new SpotOrderRequest(
            "XBTUSD", SpotOrderSide.Buy, SpotOrderType.Market, 1m, 30000m, ClientOrderId));
    }

    [Fact]
    public void AnOrderWithoutAClientOrderIdIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new SpotOrderRequest(
            "XBTUSD", SpotOrderSide.Buy, SpotOrderType.Market, 1m, null, "  "));
    }

    // ---- Placement outcomes --------------------------------------------

    [Fact]
    public async Task AValidatedOrderIsNotReportedAsAnAcceptedOrder()
    {
        // Kraken answers a validate-only request with a description and no
        // transaction id. Reporting it as accepted would invent exposure.
        using var handler = new StubHandler(
            """{"error":[],"result":{"descr":{"order":"buy 0.25 XBTUSD @ limit 30000.5"}}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy());

        Assert.Equal(SpotPlacementOutcome.Validated, result.Outcome);
        Assert.Null(result.ExchangeOrderId);
    }

    [Fact]
    public async Task AnAcceptedOrderReportsTheExchangeOrderId()
    {
        using var handler = new StubHandler(
            """{"error":[],"result":{"descr":{"order":"buy 0.25 XBTUSD @ limit 30000.5"},"txid":["OUF4EM-FRGI2-MQMWZD"]}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        Assert.Equal(SpotPlacementOutcome.Accepted, result.Outcome);
        Assert.Equal("OUF4EM-FRGI2-MQMWZD", result.ExchangeOrderId);
    }

    [Fact]
    public async Task AnOrderRefusedOnItsOwnContentIsReportedAsRejected()
    {
        // Kraken validates these before the order reaches the book, so the
        // order definitely does not exist and a corrected resubmission is safe.
        using var handler = new StubHandler("""{"error":["EOrder:Insufficient funds"]}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        Assert.Equal(SpotPlacementOutcome.Rejected, result.Outcome);
        Assert.Contains("EOrder:Insufficient funds", result.ExchangeErrors);
    }

    [Fact]
    public async Task ATimeoutIsReportedAsIndeterminateAndNeverAsRejected()
    {
        // The order may have been accepted and filled. Anything other than
        // "unknown" here is how a timeout becomes two live positions.
        using var handler = new TimingOutHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        Assert.Equal(SpotPlacementOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public async Task ANetworkFailureIsReportedAsIndeterminate()
    {
        using var handler = new OfflineHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        Assert.Equal(SpotPlacementOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public async Task ARateLimitIsReportedAsIndeterminateRatherThanRejected()
    {
        // A rate limit probably means Kraken did nothing, but "probably" is not
        // good enough to conclude that no order exists.
        using var handler = new StubHandler("""{"error":["EAPI:Rate limit exceeded"]}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        Assert.Equal(SpotPlacementOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public async Task AnUnrecognisedKrakenErrorIsReportedAsIndeterminate()
    {
        // A Kraken error this connector has never seen must not be read as
        // proof that no order was created.
        using var handler = new StubHandler("""{"error":["EGeneral:Something entirely new"]}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        Assert.Equal(SpotPlacementOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public async Task AReusedClientOrderIdIsReportedAsADuplicateRatherThanAFailure()
    {
        // This is positive evidence that an earlier submission succeeded, so
        // the caller must query that order rather than submit another.
        using var handler = new StubHandler("""{"error":["EOrder:cl_ord_id already in use"]}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        Assert.Equal(SpotPlacementOutcome.DuplicateClientOrderId, result.Outcome);
    }

    [Fact]
    public void ARejectionCannotBeConstructedWithoutTheExchangesReason()
    {
        // Without a reason a rejection is indistinguishable from an unanswered
        // request, which is exactly the distinction that matters.
        Assert.Throws<ArgumentException>(() => SpotOrderPlacement.Rejected(ClientOrderId, []));
    }

    [Fact]
    public async Task AFailureMessageNeverContainsTheCredential()
    {
        using var handler = new OfflineHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.PlaceAsync(Credential(), LimitBuy(validateOnly: false));

        var text = string.Join(" ", result.ExchangeErrors);
        Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
    }

    // ---- Cancellation ---------------------------------------------------

    [Fact]
    public async Task CancellingAnOrderKrakenDoesNotKnowReportsNotFound()
    {
        using var handler = new StubHandler("""{"error":["EOrder:Unknown order"]}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.CancelAsync(Credential(), ClientOrderId);

        Assert.Equal(SpotCancellationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task ACancellationThatMatchedNothingIsNotReportedAsCancelled()
    {
        // Kraken answers success with a count of zero when it found nothing.
        // Reading that as a cancellation would hide a still-live order.
        using var handler = new StubHandler("""{"error":[],"result":{"count":0}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.CancelAsync(Credential(), ClientOrderId);

        Assert.Equal(SpotCancellationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task ASuccessfulCancellationIsReportedAsCancelled()
    {
        using var handler = new StubHandler("""{"error":[],"result":{"count":1}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.CancelAsync(Credential(), ClientOrderId);

        Assert.Equal(SpotCancellationOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task AnUnansweredCancellationIsIndeterminateSoTheOrderStaysSuspect()
    {
        using var handler = new OfflineHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.CancelAsync(Credential(), ClientOrderId);

        Assert.Equal(SpotCancellationOutcome.Indeterminate, result.Outcome);
    }

    [Fact]
    public async Task ACancellationIsKeyedOnTheClientOrderId()
    {
        using var handler = new StubHandler("""{"error":[],"result":{"count":1}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        await gateway.CancelAsync(Credential(), ClientOrderId);

        // The exchange id is the value that goes missing when a submission
        // times out, so it cannot be the key used to clean up afterwards.
        Assert.Contains("cl_ord_id=" + ClientOrderId, handler.LastBody, StringComparison.Ordinal);
    }

    // ---- Status query ---------------------------------------------------

    [Fact]
    public async Task AQueryReportsTheFilledQuantityAsADecimal()
    {
        using var handler = new StubHandler(
            """
            {"error":[],"result":{"OBCMZD-JIEE7-77TH3F":{"status":"closed","vol":"0.25","vol_exec":"0.13579","price":"30000.5"}}}
            """);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.QueryAsync(Credential(), ClientOrderId);

        Assert.Equal(OrderStatusQueryOutcome.Found, result.Outcome);
        Assert.Equal(ExchangeOrderState.Filled, result.State);

        // Read straight into decimal. A binary floating point type cannot hold
        // this value exactly.
        Assert.Equal(0.13579m, result.FilledQuantity);
        Assert.Equal("OBCMZD-JIEE7-77TH3F", result.ExchangeOrderId);
    }

    [Fact]
    public async Task AQueryForAnOrderKrakenDoesNotHoldProvesTheOrderDoesNotExist()
    {
        using var handler = new StubHandler("""{"error":[],"result":{}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.QueryAsync(Credential(), ClientOrderId);

        Assert.Equal(OrderStatusQueryOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task AnUnreachableExchangeIsNeverReportedAsAnAbsentOrder()
    {
        // This is the single most important line in reconciliation: a network
        // problem must not look like proof that nothing was placed.
        using var handler = new OfflineHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.QueryAsync(Credential(), ClientOrderId);

        Assert.Equal(OrderStatusQueryOutcome.Unavailable, result.Outcome);
        Assert.NotEqual(OrderStatusQueryOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task AnUnrecognisedOrderStatusIsReportedAsUnavailableRatherThanGuessed()
    {
        // Guessing could report a still-open order as finished.
        using var handler = new StubHandler(
            """{"error":[],"result":{"OBCMZD-JIEE7-77TH3F":{"status":"quantum","vol_exec":"0"}}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.QueryAsync(Credential(), ClientOrderId);

        Assert.Equal(OrderStatusQueryOutcome.Unavailable, result.Outcome);
    }

    [Theory]
    [InlineData("open", ExchangeOrderState.New)]
    [InlineData("pending", ExchangeOrderState.PendingNew)]
    [InlineData("closed", ExchangeOrderState.Filled)]
    [InlineData("canceled", ExchangeOrderState.Canceled)]
    [InlineData("expired", ExchangeOrderState.Expired)]
    public void KrakenOrderStatusesMapToExchangeNeutralStates(string status, ExchangeOrderState expected) =>
        Assert.Equal(expected, KrakenSpotOrderGateway.MapOrderState(status));

    // ---- Fills -----------------------------------------------------------

    [Fact]
    public async Task FillsAreReadAsDecimalsWithUtcTimestamps()
    {
        using var handler = new StubHandler(
            """
            {"error":[],"result":{"trades":{"THVRQM-33VKH-UCI7BS":{"ordertxid":"OQCLML-BW3P3-BUCMWZ","pair":"XXBTZUSD","time":1688667012.2678,"type":"buy","price":"30010.0","cost":"600.20","fee":"0.96","vol":"0.02"}},"count":1}}
            """);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.ListFillsAsync(Credential(), Now.AddDays(-1));

        Assert.Equal(SpotFillQueryOutcome.Answered, result.Outcome);
        var fill = Assert.Single(result.Fills);

        Assert.Equal(0.02m, fill.Quantity);
        Assert.Equal(30010.0m, fill.Price);
        Assert.Equal(0.96m, fill.Fee);
        Assert.Equal(SpotOrderSide.Buy, fill.Side);
        Assert.Equal("OQCLML-BW3P3-BUCMWZ", fill.ExchangeOrderId);
        Assert.Equal(TimeSpan.Zero, fill.ExecutedAtUtc.Offset);
        Assert.Equal(2023, fill.ExecutedAtUtc.Year);
    }

    [Fact]
    public async Task AFillDoesNotClaimAFeeCurrencyKrakenNeverStated()
    {
        // Assuming the quote asset would silently misstate the cost of a trade
        // and flow straight into profit and loss.
        using var handler = new StubHandler(
            """
            {"error":[],"result":{"trades":{"THVRQM-33VKH-UCI7BS":{"ordertxid":"OQCLML-BW3P3-BUCMWZ","pair":"XXBTZUSD","time":1688667012.2678,"type":"sell","price":"30010.0","fee":"0.96","vol":"0.02"}},"count":1}}
            """);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.ListFillsAsync(Credential(), Now.AddDays(-1));

        var fill = Assert.Single(result.Fills);
        Assert.Null(fill.FeeCurrency);

        // The pair is carried so the instrument catalogue can resolve it later.
        Assert.Equal("XXBTZUSD", fill.Symbol);
    }

    [Fact]
    public async Task AnUnansweredFillQueryIsNeverReportedAsNoFills()
    {
        // An empty list from a failed query would silently erase real
        // executions from the platform's view of a position.
        using var handler = new OfflineHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        var gateway = new KrakenSpotOrderGateway(client, new FixedClock(Now));

        var result = await gateway.ListFillsAsync(Credential(), Now.AddDays(-1));

        Assert.Equal(SpotFillQueryOutcome.Unavailable, result.Outcome);
        Assert.Empty(result.Fills);
    }

    // ---- Safety properties -----------------------------------------------

    [Fact]
    public void TheGatewayExposesNoWithdrawalOrTransferOperation()
    {
        var forbidden = new[] { "withdraw", "transfer", "send", "payout" };

        var offending = typeof(KrakenSpotOrderGateway)
            .GetMethods()
            .Select(m => m.Name)
            .Concat(typeof(ISpotOrderGateway).GetMethods().Select(m => m.Name))
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void TheGatewayIsNotYetALiveExecutionRoute()
    {
        // Phase 9 delivers the ability to place, reconcile and bound a real
        // order. It deliberately does not open the promotion ladder: the gated
        // rollout, supervision and audit surface are Phase 10. The day this
        // assertion is deleted, live trading becomes reachable, and that must
        // be a decision rather than a side effect.
        Assert.False(typeof(ILiveExecutionRoute).IsAssignableFrom(typeof(KrakenSpotOrderGateway)));
    }

    [Fact]
    public void AnEmptyKrakenEnvelopeIsATransportProblemRatherThanAnAnswer()
    {
        Assert.Throws<KrakenTransportException>(
            () => KrakenSpotOrderGateway.Parse("   ", httpSucceeded: true));
    }

    [Fact]
    public void AnUnreadableKrakenBodyIsATransportProblemRatherThanAnAnswer()
    {
        Assert.Throws<KrakenTransportException>(
            () => KrakenSpotOrderGateway.Parse("<html>gateway timeout</html>", httpSucceeded: false));
    }

    // ---- Helpers ----------------------------------------------------------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _payload;

        public StubHandler(string payload) => _payload = payload;

        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class TimingOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("timed out");
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
