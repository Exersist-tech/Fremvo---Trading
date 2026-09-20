using System.Globalization;
using System.Net;
using System.Text;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
using Trading.Exchanges.Kraken.Execution;

namespace Trading.ArchitectureTests;

/// <summary>Replay-only tests for the deliberately isolated Futures demo connector.</summary>
public sealed class KrakenFuturesDemoOrderGatewayTests
{
    private const string ApiKey = "replay-key";
    private const string ApiSecret = "c2FmZS1yZXBsYXktc2VjcmV0";
    private const string ClientOrderId = "future-order-001";
    private static readonly Uri DemoUri = new("https://demo-futures.kraken.com/derivatives/api/v3/");

    [Theory]
    [InlineData("https://futures.kraken.com/derivatives/api/v3/")]
    [InlineData("https://api.kraken.com/")]
    [InlineData("https://unexpected.example/derivatives/api/v3/")]
    public void OptionsRefuseEveryHostExceptTheExactDemoApi(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new KrakenFuturesDemoOptions(new Uri(endpoint)));

    [Fact]
    public void RequestEncodingIsInvariantAndMapsClientIdAndReduceOnly()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("nb-NO");
            var body = KrakenFuturesDemoOrderGateway.BuildSendOrderBody(Request(reduceOnly: true));

            Assert.Contains("size=0.25", body, StringComparison.Ordinal);
            Assert.Contains("limitPrice=30000.5", body, StringComparison.Ordinal);
            Assert.Contains("cliOrdId=" + ClientOrderId, body, StringComparison.Ordinal);
            Assert.Contains("reduceOnly=true", body, StringComparison.Ordinal);
            Assert.Contains("validate=true", body, StringComparison.Ordinal);
            Assert.DoesNotContain(",", body, StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task DisabledGatewayDoesNotSendAnyRequest()
    {
        using var handler = new ReplayHandler("""{"result":"success"}""");
        using var client = new HttpClient(handler);
        var gateway = new KrakenFuturesDemoOrderGateway(client, new KrakenFuturesDemoOptions(DemoUri));

        var result = await gateway.PlaceAsync(Credential(), Request(reduceOnly: true));

        Assert.Equal(FuturesPlacementOutcome.Rejected, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ACallerCannotRedirectTheGatewayByChangingHttpClientBaseAddress()
    {
        using var handler = new ReplayHandler("""{"result":"success","sendStatus":{"order_id":"abc"}}""");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://futures.kraken.com/") };
        var gateway = EnabledGateway(client);

        await gateway.PlaceAsync(Credential(), Request(reduceOnly: true));

        Assert.Equal("demo-futures.kraken.com", handler.LastRequest!.RequestUri!.Host);
    }

    [Fact]
    public async Task PositionIncreasingSubmissionIsRefusedLocally()
    {
        using var handler = new ReplayHandler("""{"result":"success"}""");
        using var client = new HttpClient(handler);
        var gateway = EnabledGateway(client);

        var result = await gateway.PlaceAsync(Credential(), Request(reduceOnly: false, validateOnly: false));

        Assert.Equal(FuturesPlacementOutcome.Rejected, result.Outcome);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ExplicitReduceOnlyDemoSubmissionCanBeAccepted()
    {
        using var handler = new ReplayHandler("""{"result":"success","sendStatus":{"order_id":"demo-order"}}""");
        using var client = new HttpClient(handler);
        var gateway = EnabledGateway(client);

        var result = await gateway.PlaceAsync(Credential(), Request(reduceOnly: true, validateOnly: false));

        Assert.Equal(FuturesPlacementOutcome.Accepted, result.Outcome);
        Assert.Equal("demo-order", result.ExchangeOrderId);
    }

    [Theory]
    [InlineData("""{"result":"success","sendStatus":{"order_id":"abc"}}""", FuturesPlacementOutcome.Validated)]
    [InlineData("""{"result":"error","error":"invalidArgument"}""", FuturesPlacementOutcome.Rejected)]
    [InlineData("""{"result":"error","error":"authenticationError"}""", FuturesPlacementOutcome.Indeterminate)]
    public async Task PlacementPreservesValidatedRejectedAndIndeterminateTaxonomy(string response, FuturesPlacementOutcome expected)
    {
        using var handler = new ReplayHandler(response);
        using var client = new HttpClient(handler);
        var gateway = EnabledGateway(client);

        var result = await gateway.PlaceAsync(Credential(), Request(reduceOnly: true));

        Assert.Equal(expected, result.Outcome);
        Assert.Equal("/derivatives/api/v3/sendorder", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.True(handler.LastBody!.Contains("validate=true", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(handler.LastRequest.Headers.GetValues("Authent").Single()));
    }

    [Fact]
    public async Task CancelAndQueryUseOnlyDocumentedOrderRoutes()
    {
        using var cancelHandler = new ReplayHandler("""{"result":"success","cancelStatus":{"status":"cancelled"}}""");
        using var cancelClient = new HttpClient(cancelHandler);
        var gateway = EnabledGateway(cancelClient);
        var cancellation = await gateway.CancelAsync(Credential(), ClientOrderId);
        Assert.Equal(FuturesCancellationOutcome.Cancelled, cancellation.Outcome);
        Assert.Equal("/derivatives/api/v3/cancelorder", cancelHandler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("cliOrdId=" + ClientOrderId, cancelHandler.LastBody!, StringComparison.Ordinal);

        using var queryHandler = new ReplayHandler("""{"result":"success","openOrders":[{"cliOrdId":"future-order-001","order_id":"abc","side":"sell","filledSize":0.1,"unfilledSize":0.15,"reduceOnly":true,"lastUpdateTime":"2026-01-01T00:00:00Z"}]}""");
        using var queryClient = new HttpClient(queryHandler);
        gateway = EnabledGateway(queryClient);
        var query = await gateway.QueryAsync(Credential(), ClientOrderId);
        Assert.Equal(FuturesOrderQueryOutcome.Found, query.Outcome);
        Assert.Equal(FuturesOrderSide.Sell, query.Order!.Side);
        Assert.Equal("/derivatives/api/v3/openorders", queryHandler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, queryHandler.LastRequest.Method);
    }

    [Fact]
    public void FuturesRemainOutsideLiveRoutesAndKrakenDoesNotLeakIntoAbstractions()
    {
        Assert.False(typeof(ILiveExecutionRoute).IsAssignableFrom(typeof(KrakenFuturesDemoOrderGateway)));
        Assert.DoesNotContain(
            typeof(ILiveExecutionRoute).Assembly.GetTypes().Where(type => typeof(ILiveExecutionRoute).IsAssignableFrom(type)),
            type => type.GetConstructors().SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType == typeof(IFuturesOrderGateway)));
        Assert.DoesNotContain(typeof(IFuturesOrderGateway).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Trading.Exchanges.Kraken", StringComparison.Ordinal));
    }

    private static ExchangeCredential Credential() => new(ApiKey, ApiSecret);

    private static FuturesOrderRequest Request(bool reduceOnly, bool validateOnly = true) =>
        new("PI_XBTUSD", FuturesOrderSide.Sell, FuturesPositionDirection.LongPosition, FuturesOrderType.Limit,
            0.25m, 30000.5m, reduceOnly, ClientOrderId, validateOnly);

    private static KrakenFuturesDemoOrderGateway EnabledGateway(HttpClient client) =>
        new(client, new KrakenFuturesDemoOptions(DemoUri), KrakenFuturesDemoRoute.EnableForReplayTests());

    private sealed class ReplayHandler(string payload) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }
}
