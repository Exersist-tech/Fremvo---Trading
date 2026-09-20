using Trading.Exchanges.Kraken;

namespace Trading.ArchitectureTests;

public sealed class KrakenClientTests
{
    [Fact]
    public async Task KrakenExchangeClientCanPingAndValidateEnvironment()
    {
        var client = new KrakenExchangeClient();

        var ping = await client.PingAsync();
        var serverTime = await client.GetServerTimeAsync();
        var validation = await client.ValidateApiKeyAsync("api-key", "api-secret", "proving");

        Assert.Equal("pong", ping);
        Assert.Equal("server-time-ok", serverTime);
        Assert.Equal("validated:proving", validation);
    }

    [Fact]
    public async Task KrakenExchangeClientRejectsMissingCredentials()
    {
        var client = new KrakenExchangeClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ValidateApiKeyAsync(string.Empty, "api-secret", "proving").AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => client.ValidateApiKeyAsync("api-key", string.Empty, "proving").AsTask());
    }
}
