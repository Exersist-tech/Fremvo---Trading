namespace Trading.Exchanges.Kraken;

/// <summary>
/// Minimal Kraken connectivity surface.
/// </summary>
/// <remarks>
/// Deliberately exposes no withdrawal or transfer operation. The platform
/// never moves user funds, so no such method exists to be called by mistake.
/// </remarks>
public interface IKrakenExchangeClient
{
    ValueTask<string> GetServerTimeAsync(CancellationToken cancellationToken = default);

    ValueTask<string> PingAsync(CancellationToken cancellationToken = default);

    ValueTask<string> ValidateApiKeyAsync(
        string apiKey,
        string apiSecret,
        string environment,
        CancellationToken cancellationToken = default);
}

public sealed class KrakenExchangeClient : IKrakenExchangeClient
{
    public ValueTask<string> GetServerTimeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult("server-time-ok");
    }

    public ValueTask<string> PingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult("pong");
    }

    public ValueTask<string> ValidateApiKeyAsync(
        string apiKey,
        string apiSecret,
        string environment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        if (string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new ArgumentException("API secret is required.", nameof(apiSecret));
        }

        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new ArgumentException("Environment is required.", nameof(environment));
        }

        // The environment is echoed, never the key or the secret. Neither
        // credential may appear in a return value, a log, or an error.
        return ValueTask.FromResult($"validated:{environment}");
    }
}
