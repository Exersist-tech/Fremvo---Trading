using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Trading.Exchanges.Abstractions;

namespace Trading.Exchanges.Kraken;

/// <summary>
/// Determines what a Kraken API credential is permitted to do by asking Kraken.
/// </summary>
/// <remarks>
/// <para>
/// Kraken exposes no endpoint that describes a key's permissions, so each
/// capability is established by calling an endpoint that requires it and
/// reading the error Kraken returns. Kraken answers <c>EGeneral:Permission
/// denied</c> when a key lacks the permission for a call.
/// </para>
/// <para>
/// The withdrawal check calls <c>WithdrawMethods</c>, which only lists the
/// methods available. It moves no funds. Its sole purpose is to detect that
/// the key holds withdrawal permission so the connection can be refused. The
/// platform has no code that performs a withdrawal.
/// </para>
/// <para>
/// The trade check calls <c>AddOrder</c> with Kraken's <c>validate</c> flag,
/// which Kraken documents as validating inputs without submitting an order.
/// The flag is a hard-coded constant with no parameter that can switch it off,
/// and a test asserts every generated AddOrder body contains it.
/// </para>
/// </remarks>
public sealed class KrakenPermissionProbe : IExchangePermissionProbe
{
    internal const string BalancePath = "/0/private/Balance";
    internal const string WithdrawMethodsPath = "/0/private/WithdrawMethods";
    internal const string AddOrderPath = "/0/private/AddOrder";

    /// <summary>
    /// Kraken's flag for "check this order but do not submit it". It is a
    /// constant on purpose: there is no code path that can place a real order
    /// while probing a credential.
    /// </summary>
    internal const string ValidateOnlyFlag = "validate=true";

    private const string PermissionDeniedError = "EGeneral:Permission denied";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public KrakenPermissionProbe(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ExchangeKind Exchange => ExchangeKind.Kraken;

    /// <summary>
    /// Builds the body used for the trade-permission check. Exposed to tests so
    /// the validate-only flag can be asserted.
    /// </summary>
    internal static string BuildTradeProbeBody(string nonce) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"nonce={nonce}&{ValidateOnlyFlag}&ordertype=limit&type=buy&volume=0.0001&pair=XBTUSD&price=1");

    public async Task<ApiPermissionSnapshot> ProbeAsync(
        ExchangeCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var canRead = await HasPermissionAsync(
            credential,
            BalancePath,
            nonce => $"nonce={nonce}",
            cancellationToken).ConfigureAwait(false);

        var canWithdraw = await HasPermissionAsync(
            credential,
            WithdrawMethodsPath,
            nonce => $"nonce={nonce}",
            cancellationToken).ConfigureAwait(false);

        // The trade check is skipped when the key can withdraw, because the
        // connection will be refused regardless and there is no reason to send
        // a further request with a credential the platform is rejecting.
        var canTrade = !canWithdraw
            && await HasPermissionAsync(
                credential,
                AddOrderPath,
                BuildTradeProbeBody,
                cancellationToken).ConfigureAwait(false);

        return new ApiPermissionSnapshot(
            CanRead: canRead,
            CanTrade: canTrade,
            CanWithdraw: canWithdraw,
            ValidatedAtUtc: _timeProvider.GetUtcNow());
    }

    private async Task<bool> HasPermissionAsync(
        ExchangeCredential credential,
        string path,
        Func<string, string> buildBody,
        CancellationToken cancellationToken)
    {
        var nonce = KrakenRequestSigner.CreateNonce(_timeProvider.GetUtcNow());
        var body = buildBody(nonce);
        var signature = KrakenRequestSigner.Sign(path, nonce, body, credential.ApiSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.TryAddWithoutValidation("API-Key", credential.ApiKey);
        request.Headers.TryAddWithoutValidation("API-Sign", signature);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ExchangePermissionProbeException(
                "Kraken could not be reached to check this API key. No credential detail is included in this message.",
                exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExchangePermissionProbeException(
                "The request to Kraken timed out while checking this API key.",
                exception);
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return InterpretResponse(payload);
        }
    }

    /// <summary>
    /// Reads a Kraken response and reports whether the key holds the permission
    /// the call required.
    /// </summary>
    /// <remarks>
    /// Kraken answers HTTP 200 even for failures and reports problems in its
    /// <c>error</c> array, so the status code is not consulted. An unrecognised
    /// error is treated as "permission not held", which fails closed.
    /// </remarks>
    internal static bool InterpretResponse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new ExchangePermissionProbeException("Kraken returned an empty response while checking an API key.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new ExchangePermissionProbeException(
                "Kraken returned a response that could not be read while checking an API key.",
                exception);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("error", out var errors)
                || errors.ValueKind != JsonValueKind.Array)
            {
                throw new ExchangePermissionProbeException(
                    "Kraken returned a response without an error field while checking an API key.");
            }

            if (errors.GetArrayLength() == 0)
            {
                return true;
            }

            foreach (var error in errors.EnumerateArray())
            {
                var text = error.GetString();

                if (string.Equals(text, PermissionDeniedError, StringComparison.Ordinal))
                {
                    return false;
                }

                // An invalid key or signature is a credential problem rather
                // than a permission answer, and must not be reported as
                // "permission absent" because that would be misleading.
                if (text is not null
                    && (text.Contains("Invalid key", StringComparison.Ordinal)
                        || text.Contains("Invalid signature", StringComparison.Ordinal)
                        || text.Contains("Invalid nonce", StringComparison.Ordinal)))
                {
                    throw new ExchangePermissionProbeException(
                        "Kraken rejected this API key or its signature. Check that the key and secret were copied exactly.");
                }
            }

            return false;
        }
    }
}
