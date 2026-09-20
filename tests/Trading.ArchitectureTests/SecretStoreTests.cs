using Trading.Infrastructure.Secrets;

namespace Trading.ArchitectureTests;

public sealed class SecretStoreTests
{
    [Fact]
    public async Task InMemorySecretStoreStoresAndReadsSecretMetadata()
    {
        var store = new InMemorySecretStore();

        await store.StoreSecretAsync("kraken-proving-key", "super-secret-value", "v2");

        var readSecret = await store.GetSecretAsync("kraken-proving-key");
        var version = await store.GetSecretVersionAsync("kraken-proving-key");

        Assert.Equal("super-secret-value", readSecret);
        Assert.Equal("v2", version);
    }

    [Fact]
    public async Task InMemorySecretStoreRemovesSecret()
    {
        var store = new InMemorySecretStore();

        await store.StoreSecretAsync("kraken-live-key", "livesecret");
        await store.RemoveSecretAsync("kraken-live-key");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetSecretAsync("kraken-live-key").AsTask());
    }
}