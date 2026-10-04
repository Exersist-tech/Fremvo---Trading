using System.Net;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Kraken;
using Trading.Exchanges.Kraken.Account;

namespace Trading.ArchitectureTests;

public sealed class KrakenBalanceGatewayTests
{
    [Fact]
    public void ParseNormalizesKrakenAssetCodes()
    {
        var at = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        var snapshot = KrakenBalanceGateway.Parse(
            """{"error":[],"result":{"XXBT":{"balance":"1.2500","hold_trade":"0.25"},"ZUSD":{"balance":"42.10","hold_trade":"0"},"XDG":{"balance":"7","credit":"2","credit_used":"0.5","hold_trade":"1"}}}""", at);

        Assert.Equal(at, snapshot.RetrievedAtUtc);
        Assert.Collection(snapshot.Balances,
            balance => { Assert.Equal("BTC", balance.Asset); Assert.Equal("XXBT", balance.VenueAsset); Assert.Equal(1.25m, balance.Total); Assert.Equal(1m, balance.Available); Assert.Equal(0.25m, balance.Held); },
            balance => { Assert.Equal("USD", balance.Asset); Assert.Equal("ZUSD", balance.VenueAsset); Assert.Equal(42.10m, balance.Total); Assert.Equal(42.10m, balance.Available); Assert.Equal(0m, balance.Held); },
            balance => { Assert.Equal("DOGE", balance.Asset); Assert.Equal(7m, balance.Total); Assert.Equal(7.5m, balance.Available); Assert.Equal(1m, balance.Held); });
    }

    [Theory]
    [InlineData("""{"error":[],"result":{"XXBT":"1.25"}}""")]
    [InlineData("""{"error":[],"result":{"XXBT":{"balance":"1.25"}}}""")]
    [InlineData("""{"error":[],"result":{"XXBT":{"balance":"1.25","hold_trade":"1,000"}}}""")]
    [InlineData("""{"error":[],"result":{"XXBT":{"balance":"1","hold_trade":"0"},"XBT":{"balance":"2","hold_trade":"0"}}}""")]
    [InlineData("""{"error":[],"result":{"XXBT":{"balance":"1","hold_trade":"-1"}}}""")]
    public void ParseRefusesMalformedOrAmbiguousBalances(string payload)
    {
        Assert.Throws<Trading.Exchanges.Abstractions.Account.ExchangeBalanceReadException>(() =>
            KrakenBalanceGateway.Parse(payload, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseRefusesAnExchangeErrorInsteadOfReturningBalances()
    {
        Assert.Throws<Trading.Exchanges.Abstractions.Account.ExchangeBalanceReadException>(() =>
            KrakenBalanceGateway.Parse(
                """{"error":["EAPI:Invalid key"],"result":{"XXBT":{"balance":"1","hold_trade":"0"}}}""",
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ReadUsesSignedExtendedBalanceWithoutRealNetworkContact()
    {
        using var handler = new BalanceResponseHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://kraken-balance-replay.invalid")
        };
        var gateway = new KrakenBalanceGateway(client, new KrakenNonceSource(TimeProvider.System),
            TimeProvider.System);

        var result = await gateway.ReadBalancesAsync(
            new ExchangeCredential("test-key", Convert.ToBase64String(new byte[64])));

        Assert.Equal("/0/private/BalanceEx", handler.Path);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.True(handler.HasSignature);
        Assert.Equal(1m, Assert.Single(result.Balances).Available);
    }

    private sealed class BalanceResponseHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public bool HasSignature { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Method = request.Method;
            HasSignature = request.Headers.Contains("API-Sign");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"error":[],"result":{"ZUSD":{"balance":"2","hold_trade":"1"}}}""")
            });
        }
    }
}
