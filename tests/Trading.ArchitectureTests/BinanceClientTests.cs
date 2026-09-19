using Trading.Exchanges.Binance;

namespace Trading.ArchitectureTests;

public sealed class BinanceClientTests
{
    [Fact]
    public async Task BinanceExchangeClientCanPingAndValidateEnvironment()
    {
        var client = new BinanceExchangeClient();

        var ping = await client.PingAsync();
        var serverTime = await client.GetServerTimeAsync();
        var validation = await client.ValidateApiKeyAsync("api-key", "api-secret", "testnet");

        Assert.Equal("pong", ping);
        Assert.Equal("server-time-ok", serverTime);
        Assert.Equal("validated:testnet", validation);
    }

    [Fact]
    public async Task BinanceExchangeClientRejectsMissingCredentials()
    {
        var client = new BinanceExchangeClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ValidateApiKeyAsync(string.Empty, "api-secret", "testnet").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => client.ValidateApiKeyAsync("api-key", string.Empty, "testnet").AsTask());
    }
}
