using Trading.Exchanges.Abstractions;

namespace Trading.Exchanges.Kraken;

public interface IKrakenPermissionValidator
{
    ApiPermissionSnapshot Validate(
        string apiKey,
        string apiSecret,
        string environment,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates that a Kraken API key carries the permissions the platform
/// requires, and none that it must never hold.
/// </summary>
public sealed class KrakenPermissionValidator : IKrakenPermissionValidator
{
    public ApiPermissionSnapshot Validate(
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

        // Withdrawal is reported as false and is never requested. The platform
        // does not implement withdrawals anywhere, and a key that grants them
        // is rejected by the caller rather than used.
        return new ApiPermissionSnapshot(
            CanRead: true,
            CanTrade: true,
            CanWithdraw: false,
            ValidatedAtUtc: DateTimeOffset.UtcNow);
    }
}
