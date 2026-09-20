using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Trading.Exchanges.Kraken;

/// <summary>
/// Signs Kraken private API requests.
/// </summary>
/// <remarks>
/// Kraken authenticates a private request with
/// <c>HMAC-SHA512(base64decode(secret), path + SHA256(nonce + postData))</c>,
/// base64 encoded. The signature is verified against Kraken's own published
/// test vector in the architecture tests, so a change to this method that
/// broke authentication would fail the build rather than appear at runtime as
/// an unexplained permission error.
/// </remarks>
public static class KrakenRequestSigner
{
    /// <summary>
    /// Produces the value for the <c>API-Sign</c> header.
    /// </summary>
    /// <param name="requestPath">The private request path, for example <c>/0/private/Balance</c>.</param>
    /// <param name="nonce">The nonce included in <paramref name="postData"/>.</param>
    /// <param name="postData">The full form-encoded request body.</param>
    /// <param name="apiSecret">The base64 Kraken API secret.</param>
    public static string Sign(string requestPath, string nonce, string postData, string apiSecret)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            throw new ArgumentException("Request path is required.", nameof(requestPath));
        }

        if (string.IsNullOrWhiteSpace(nonce))
        {
            throw new ArgumentException("Nonce is required.", nameof(nonce));
        }

        ArgumentNullException.ThrowIfNull(postData);

        if (string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new ArgumentException("API secret is required.", nameof(apiSecret));
        }

        byte[] decodedSecret;
        try
        {
            decodedSecret = Convert.FromBase64String(apiSecret);
        }
        catch (FormatException exception)
        {
            // The malformed value is not included: it is the secret itself.
            throw new FormatException(
                "The Kraken API secret is not valid base64. Copy it exactly as Kraken issued it.",
                exception);
        }

        var nonceAndData = Encoding.UTF8.GetBytes(nonce + postData);
        var hashed = SHA256.HashData(nonceAndData);

        var pathBytes = Encoding.UTF8.GetBytes(requestPath);
        var message = new byte[pathBytes.Length + hashed.Length];
        Buffer.BlockCopy(pathBytes, 0, message, 0, pathBytes.Length);
        Buffer.BlockCopy(hashed, 0, message, pathBytes.Length, hashed.Length);

        using var hmac = new HMACSHA512(decodedSecret);
        return Convert.ToBase64String(hmac.ComputeHash(message));
    }

    /// <summary>
    /// Creates a nonce. Kraken requires a value that never decreases for a
    /// given key, so this uses milliseconds since the Unix epoch.
    /// </summary>
    public static string CreateNonce(DateTimeOffset nowUtc) =>
        nowUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
}
