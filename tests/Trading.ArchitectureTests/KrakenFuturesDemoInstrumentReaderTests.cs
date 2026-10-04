using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Trading.Exchanges.Kraken.Execution;

namespace Trading.ArchitectureTests;

public sealed class KrakenFuturesDemoInstrumentReaderTests
{
    private static readonly KrakenFuturesDemoOptions Options =
        new(new Uri("https://demo-futures.kraken.com/derivatives/api/v3/"));

    private const string Instrument = """
        {"symbol":"PF_XBTUSD","pair":"XBT:USD","base":"XBT","quote":"USD",
         "type":"flexible_futures","tradeable":true,"isExpired":false,"tradfi":false,
         "contractSize":0.01,"tickSize":0.5,"contractValueTradePrecision":4,
         "maxPositionSize":100}
        """;

    [Fact]
    public async Task ReadsOnlyPublicDemoSpecificationsWithoutInferringNotional()
    {
        using var handler = new ReplayHandler(Snapshot(Instrument));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://futures.kraken.com/") };
        var before = DateTimeOffset.UtcNow;

        var observed = await new KrakenFuturesDemoInstrumentReader(client, Options).GetAsync("PF_XBTUSD");

        Assert.Equal("PF_XBTUSD", observed.Symbol);
        Assert.Equal("XBT:USD", observed.Pair);
        Assert.Equal("USD", observed.Quote);
        Assert.Equal(0.01m, observed.ContractSize);
        Assert.Equal(0.5m, observed.TickSize);
        Assert.Equal(4, observed.ContractValueTradePrecision);
        Assert.Equal(100m, observed.MaxPositionSize);
        Assert.InRange(observed.ObservedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("demo-futures.kraken.com", handler.LastRequest.RequestUri!.Host);
        Assert.Equal("/derivatives/api/v3/instruments", handler.LastRequest.RequestUri.AbsolutePath);
        Assert.Contains("contractType=flexible_futures", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest.Headers.Authorization);
    }

    [Theory]
    [InlineData("PI_XBTUSD")]
    [InlineData("PF_XBTUSD?contractType=all")]
    [InlineData("pf_xbtusd")]
    public async Task RefusesUnknownSymbolShapesWithoutHttp(string symbol)
    {
        using var handler = new ReplayHandler(Snapshot(Instrument));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new KrakenFuturesDemoInstrumentReader(client, Options).GetAsync(symbol));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task RefusesCredentialedClientsBeforeSendingPublicRequest()
    {
        using var handler = new ReplayHandler(Snapshot(Instrument));
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "do-not-send");
        var reader = new KrakenFuturesDemoInstrumentReader(client, Options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetAsync("PF_XBTUSD"));
        Assert.Equal(0, handler.CallCount);

        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("APIKey", "do-not-send");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetAsync("PF_XBTUSD"));
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("""{"result":"error","instruments":[]}""")]
    [InlineData("""{"result":"success","instruments":[]}""")]
    [InlineData("""{"result":"success","instruments":[{"symbol":"PF_XBTUSD"}]}""")]
    [InlineData("""{"result":"success","instruments":[{"symbol":"PF_XBTUSD","pair":"XBT:USD","quote":"USD","type":"futures_inverse","tradeable":true,"isExpired":false,"tradfi":false,"contractSize":1,"tickSize":1,"contractValueTradePrecision":4,"maxPositionSize":10}]}""")]
    public async Task MissingOrUnsupportedInstrumentsFailClosed(string response)
    {
        using var handler = new ReplayHandler(response);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new KrakenFuturesDemoInstrumentReader(client, Options).GetAsync("PF_XBTUSD"));
    }

    [Theory]
    [InlineData("\"isExpired\":false", "\"isExpired\":true")]
    [InlineData("\"tradeable\":true", "\"tradeable\":false")]
    [InlineData("\"tradfi\":false", "\"tradfi\":true")]
    [InlineData("\"contractSize\":0.01", "\"contractSize\":0")]
    [InlineData("\"contractSize\":0.01", "\"contractSize\":1e100")]
    [InlineData("\"tickSize\":0.5", "\"tickSize\":0")]
    [InlineData("\"quote\":\"USD\"", "\"quote\":\"EUR\"")]
    [InlineData("\"contractValueTradePrecision\":4", "\"contractValueTradePrecision\":30")]
    public async Task UnusableVenueFactsFailClosed(string original, string replacement)
    {
        using var handler = new ReplayHandler(Snapshot(Instrument.Replace(original, replacement, StringComparison.Ordinal)));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new KrakenFuturesDemoInstrumentReader(client, Options).GetAsync("PF_XBTUSD"));
    }

    [Fact]
    public async Task AmbiguousDuplicateAndExpiringSpecificationsFailClosed()
    {
        foreach (var response in new[]
        {
            Snapshot(Instrument + "," + Instrument),
            Snapshot(Instrument.Replace("\"maxPositionSize\":100", "\"maxPositionSize\":100,\"lastTradingTime\":\"2027-01-01T00:00:00Z\"", StringComparison.Ordinal))
        })
        {
            using var handler = new ReplayHandler(response);
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new KrakenFuturesDemoInstrumentReader(client, Options).GetAsync("PF_XBTUSD"));
        }
    }

    [Fact]
    public async Task NonSuccessHttpAndOversizedResponseCannotProduceEvidence()
    {
        using var failure = new ReplayHandler("{}", HttpStatusCode.ServiceUnavailable);
        using var failedClient = new HttpClient(failure);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new KrakenFuturesDemoInstrumentReader(failedClient, Options).GetAsync("PF_XBTUSD"));

        using var oversized = new ReplayHandler(new string('x', 4 * 1024 * 1024 + 1));
        using var oversizedClient = new HttpClient(oversized);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new KrakenFuturesDemoInstrumentReader(oversizedClient, Options).GetAsync("PF_XBTUSD"));
    }

    [Fact]
    public async Task ARedirectedResponseCannotBeAcceptedAsDemoEvidence()
    {
        using var handler = new ReplayHandler(Snapshot(Instrument),
            responseUri: new Uri("https://futures.kraken.com/derivatives/api/v3/instruments"));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new KrakenFuturesDemoInstrumentReader(client, Options).GetAsync("PF_XBTUSD"));
    }

    private static string Snapshot(string instrument) =>
        """{"result":"success","instruments":[""" + instrument + "]}";

    private sealed class ReplayHandler(
        string content,
        HttpStatusCode status = HttpStatusCode.OK,
        Uri? responseUri = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = responseUri is null
                    ? request : new HttpRequestMessage(HttpMethod.Get, responseUri),
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
