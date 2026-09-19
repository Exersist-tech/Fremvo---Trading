namespace Trading.Exchanges.Binance;

public interface IBinanceExchangeClient
{
    ValueTask<string> GetServerTimeAsync(CancellationToken cancellationToken = default);
    ValueTask<string> PingAsync(CancellationToken cancellationToken = default);
    ValueTask<string> ValidateApiKeyAsync(string apiKey, string apiSecret, string environment, CancellationToken cancellationToken = default);
}

public sealed class BinanceExchangeClient : IBinanceExchangeClient
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

    public ValueTask<string> ValidateApiKeyAsync(string apiKey, string apiSecret, string environment, CancellationToken cancellationToken = default)
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

        return ValueTask.FromResult($"validated:{environment}");
    }
}
