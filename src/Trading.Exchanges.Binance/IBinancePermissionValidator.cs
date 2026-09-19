using Trading.Exchanges.Abstractions;

namespace Trading.Exchanges.Binance;

public interface IBinancePermissionValidator
{
    ApiPermissionSnapshot Validate(string apiKey, string apiSecret, string environment, CancellationToken cancellationToken = default);
}

public sealed class BinancePermissionValidator : IBinancePermissionValidator
{
    public ApiPermissionSnapshot Validate(string apiKey, string apiSecret, string environment, CancellationToken cancellationToken = default)
    {
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

        var now = DateTimeOffset.UtcNow;
        var isTestnet = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase);

        var snapshot = new ApiPermissionSnapshot(
            CanRead: true,
            CanTrade: true,
            CanWithdraw: false,
            ValidatedAtUtc: now);

        if (isTestnet)
        {
            return snapshot;
        }

        return snapshot with { CanTrade = true, CanWithdraw = false };
    }
}
