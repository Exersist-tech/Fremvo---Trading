using System.Net;
using System.Text.Json;
using Trading.Exchanges.Kraken.MarketData;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class KrakenPublicTickerQuoteSourceTests
{
    [Fact]
    public async Task RequestsOnlyTheSelectedPairUsingKrakenPublicDisplayAliases()
    {
        Uri? requestedUri = null;
        using var handler = new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"error":[],"result":{"BTC/USD":{"b":["85000"],"a":["85001"]}}}
                    """)
            };
        });
        using var client = new HttpClient(handler, disposeHandler: false)
        { BaseAddress = new Uri("https://api.kraken.com/0/") };
        var quote = await new KrakenPublicTickerQuoteSource(client).GetAsync("XBT/USD", CancellationToken.None);
        Assert.Equal("/0/public/Ticker?pair=BTC%2FUSD&assetVersion=1", requestedUri?.PathAndQuery);
        Assert.Equal("XBT/USD", quote.Symbol);
        Assert.Equal(85000.5m, quote.Price);
    }

    [Fact]
    public void CalculatesDecimalMidquoteForOnlyTheRequestedPair()
    {
        using var payload = JsonDocument.Parse("""
            {"error":[],"result":{"BTC/USD":{"b":["85000.00000001"],"a":["85000.00000003"]}}}
            """);
        var received = new DateTimeOffset(2026, 10, 4, 9, 40, 0, TimeSpan.Zero);
        var quote = KrakenPublicTickerQuoteSource.Parse(payload.RootElement, "XBT/USD", received);
        Assert.Equal("XBT/USD", quote.Symbol);
        Assert.Equal(85000.00000002m, quote.Price);
        Assert.Equal(received, quote.RetrievedAtUtc);
    }

    [Theory]
    [InlineData("""{"error":["EQuery:Unknown asset pair"],"result":{}}""")]
    [InlineData("""{"error":[],"result":{}}""")]
    [InlineData("""{"error":[],"result":{"ETH/USD":{"b":["10"],"a":["11"]}}}""")]
    [InlineData("""{"error":[],"result":{"BTC/USD":{"b":["10"],"a":["9"]}}}""")]
    [InlineData("""{"error":[],"result":{"BTC/USD":{"b":["0"],"a":["9"]}}}""")]
    [InlineData("""{"error":[],"result":{"BTC/USD":{"b":["10"],"a":[]}}}""")]
    [InlineData("""{"error":[],"result":{"BTC/USD":{"b":["bad"],"a":["11"]}}}""")]
    [InlineData("""{"error":[],"result":{"BTC/USD":{}}}""")]
    public void RejectsUnusableQuotes(string json)
    {
        using var payload = JsonDocument.Parse(json);
        Assert.Throws<MarketDataSourceException>(() =>
            KrakenPublicTickerQuoteSource.Parse(payload.RootElement, "XBT/USD", DateTimeOffset.UtcNow));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
