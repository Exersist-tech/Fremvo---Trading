using Trading.Infrastructure.Secrets;

namespace Trading.ArchitectureTests;

public sealed class SecretStoreTests
{
    [Fact]
    public async Task InMemorySecretStoreStoresAndReadsSecretMetadata()
    {
        var store = new InMemorySecretStore();

        await store.StoreSecretAsync("binance-testnet-key", "super-secret-value", "v2");

        var readSecret = await store.GetSecretAsync("binance-testnet-key");
        var version = await store.GetSecretVersionAsync("binance-testnet-key");

        Assert.Equal("super-secret-value", readSecret);
        Assert.Equal("v2", version);
    }

    [Fact]
    public async Task InMemorySecretStoreRemovesSecret()
    {
        var store = new InMemorySecretStore();

        await store.StoreSecretAsync("binance-live-key", "livesecret");
        await store.RemoveSecretAsync("binance-live-key");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetSecretAsync("binance-live-key").AsTask());
    }
}