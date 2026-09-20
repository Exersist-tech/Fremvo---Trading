namespace Trading.Exchanges.Abstractions;

/// <summary>
/// An exchange API credential in transit.
/// </summary>
/// <remarks>
/// This type exists only to carry a credential from the request that supplied
/// it to the secret store that will hold it. It is never persisted to SQL,
/// never logged, never serialized into a response, and never cached. It
/// deliberately does not override <see cref="object.ToString"/> with its
/// contents; the override below exists so that an accidental interpolation
/// cannot print the secret.
/// </remarks>
public sealed class ExchangeCredential
{
    public ExchangeCredential(string apiKey, string apiSecret)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        if (string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new ArgumentException("API secret is required.", nameof(apiSecret));
        }

        ApiKey = apiKey.Trim();
        ApiSecret = apiSecret.Trim();
    }

    public string ApiKey { get; }

    public string ApiSecret { get; }

    /// <summary>
    /// A short, non-reversible hint used to tell two connected keys apart in
    /// the user interface. It exposes the last four characters of the public
    /// API key only. The secret never contributes to this value.
    /// </summary>
    public string PublicKeyHint => ApiKey.Length <= 4
        ? new string('*', ApiKey.Length)
        : string.Concat("****", ApiKey.AsSpan(ApiKey.Length - 4));

    /// <summary>
    /// Returns a redacted description. Overridden so that logging or
    /// interpolating a credential cannot leak it.
    /// </summary>
    public override string ToString() => $"ExchangeCredential({PublicKeyHint})";
}

/// <summary>
/// Raised by a connector when a credential could not be checked. Implementations
/// must ensure the message contains no credential material.
/// </summary>
public sealed class ExchangePermissionProbeException : Exception
{
    public ExchangePermissionProbeException()
    {
    }

    public ExchangePermissionProbeException(string message)
        : base(message)
    {
    }

    public ExchangePermissionProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Checks what an exchange API credential is actually allowed to do.
/// </summary>
/// <remarks>
/// Implemented per exchange inside that exchange's connector. The connect flow
/// depends on this neutral port so that no exchange-specific type reaches the
/// application layer.
/// </remarks>
public interface IExchangePermissionProbe
{
    /// <summary>The exchange this probe understands.</summary>
    ExchangeKind Exchange { get; }

    /// <summary>
    /// Reports the permissions the credential carries. Implementations must
    /// never exercise a withdrawal, only detect that the capability exists.
    /// </summary>
    Task<ApiPermissionSnapshot> ProbeAsync(
        ExchangeCredential credential,
        CancellationToken cancellationToken = default);
}
