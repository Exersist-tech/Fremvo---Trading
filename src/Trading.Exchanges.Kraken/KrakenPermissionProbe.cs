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

    private const string PermissionDeniedError = "Permission denied";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly IKrakenNonceSource _nonceSource;

    public KrakenPermissionProbe(
        HttpClient httpClient,
        TimeProvider timeProvider,
        IKrakenNonceSource? nonceSource = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        // A probe makes three private calls in sequence, so it needs a nonce
        // source that advances between them. One is created here when none is
        // supplied so a probe is never constructed without one.
        _nonceSource = nonceSource ?? new KrakenNonceSource(_timeProvider);
    }

    public ExchangeKind Exchange => ExchangeKind.Kraken;

    /// <summary>
    /// Builds the body used for the trade-permission check. Exposed to tests so
    /// the validate-only flag can be asserted.
    /// </summary>
    /// <remarks>
    /// The order is sized to clear Kraken's minimum order quantity and minimum
    /// order cost. An order below either minimum is rejected on its parameters,
    /// which produced a rejection that had nothing to do with the key. The bid
    /// is deliberately far below market so that, even though
    /// <c>validate=true</c> means Kraken never places it, the order would rest
    /// rather than execute if the flag were ever absent.
    /// </remarks>
    internal static string BuildTradeProbeBody(string nonce) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"nonce={nonce}&{ValidateOnlyFlag}&ordertype=limit&type=buy&volume=0.0002&pair=XBTUSD&price=50000.0");

    public async Task<ApiPermissionSnapshot> ProbeAsync(
        ExchangeCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var read = await AskAsync(
            credential,
            BalancePath,
            nonce => $"nonce={nonce}",
            cancellationToken).ConfigureAwait(false);

        var canRead = Conclude(read, "read account data");

        var withdraw = await AskAsync(
            credential,
            WithdrawMethodsPath,
            nonce => $"nonce={nonce}",
            cancellationToken).ConfigureAwait(false);

        var canWithdraw = Conclude(withdraw, "withdraw funds");

        // The trade check is skipped when the key can withdraw, because the
        // connection will be refused regardless and there is no reason to send
        // a further request with a credential the platform is rejecting.
        var canTrade = false;
        if (!canWithdraw)
        {
            var trade = await AskAsync(
                credential,
                AddOrderPath,
                BuildTradeProbeBody,
                cancellationToken).ConfigureAwait(false);

            canTrade = ConcludeTrade(trade);
        }

        return new ApiPermissionSnapshot(
            CanRead: canRead,
            CanTrade: canTrade,
            CanWithdraw: canWithdraw,
            ValidatedAtUtc: _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Turns an answer into a yes or no, refusing to guess when Kraken did not
    /// actually answer the question.
    /// </summary>
    private static bool Conclude(KrakenPermissionAnswer answer, string capability)
    {
        if (answer.Granted)
        {
            return true;
        }

        if (answer.Denied)
        {
            return false;
        }

        throw new ExchangePermissionProbeException(
            $"Kraken did not say whether this key may {capability}. It answered: " +
            $"{string.Join(", ", answer.Errors)}. The key was not stored. This is not a statement about the " +
            "key's permissions, so try again before changing anything on Kraken.");
    }

    /// <summary>
    /// Decides whether the key may place orders.
    /// </summary>
    /// <remarks>
    /// Kraken checks a key's permission before it validates an order's
    /// parameters, so a parameter complaint means the permission check already
    /// passed. Treating such a rejection as an absent permission is what made a
    /// key with "Create &amp; modify orders" enabled report that it could not
    /// place orders: the probe's test order was refused on its size or cost,
    /// not on the key.
    /// </remarks>
    private static bool ConcludeTrade(KrakenPermissionAnswer answer)
    {
        if (answer.Granted)
        {
            return true;
        }

        if (answer.Denied)
        {
            return false;
        }

        if (answer.Errors.Any(IsOrderParameterComplaint))
        {
            return true;
        }

        throw new ExchangePermissionProbeException(
            "Kraken did not say whether this key may place orders. It answered: " +
            $"{string.Join(", ", answer.Errors)}. The key was not stored. This is not a statement about the " +
            "key's permissions, so try again before changing anything on Kraken.");
    }

    /// <summary>
    /// True when Kraken rejected the probe order itself rather than the key.
    /// </summary>
    internal static bool IsOrderParameterComplaint(string error) =>
        error.StartsWith("EOrder:", StringComparison.Ordinal)
        || error.Contains("Invalid arguments", StringComparison.Ordinal)
        || error.Contains("Insufficient funds", StringComparison.Ordinal)
        || error.Contains("Invalid price", StringComparison.Ordinal)
        || error.Contains("Invalid volume", StringComparison.Ordinal)
        || error.Contains("minimum not met", StringComparison.OrdinalIgnoreCase);

    private async Task<KrakenPermissionAnswer> AskAsync(
        ExchangeCredential credential,
        string path,
        Func<string, string> buildBody,
        CancellationToken cancellationToken)
    {
        var nonce = _nonceSource.NextNonce();
        var body = buildBody(nonce);

        string signature;
        try
        {
            signature = KrakenRequestSigner.Sign(path, nonce, body, credential.ApiSecret);
        }
        catch (FormatException exception)
        {
            // A secret that is not base64 is a paste problem, not a fault. It
            // is reported as an expected outcome so the user is told what to
            // fix rather than being shown a server error.
            throw new ExchangeCredentialFormatException(
                "The private key is not in the format Kraken issues. Kraken shows it as a base64 string, " +
                "usually ending in '=='. Copy the whole value, with no spaces or line breaks, from the " +
                "private key field shown when the key was created.",
                exception);
        }

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
    /// What a Kraken response says about the permission the call required.
    /// </summary>
    /// <param name="Granted">True only when Kraken accepted the call.</param>
    /// <param name="Denied">
    /// True only when Kraken explicitly answered that the permission is absent.
    /// When both this and <paramref name="Granted"/> are false the response did
    /// not answer the question and no conclusion may be drawn from it.
    /// </param>
    /// <param name="Errors">
    /// Kraken's own error codes, for example <c>EOrder:Insufficient funds</c>.
    /// These are Kraken's fixed codes and carry no credential material, so they
    /// are safe to show the user and are the only way to explain an
    /// inconclusive result.
    /// </param>
    internal readonly record struct KrakenPermissionAnswer(
        bool Granted,
        bool Denied,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// Reads a Kraken response and reports whether the key holds the permission
    /// the call required.
    /// </summary>
    /// <remarks>
    /// Kraken answers HTTP 200 even for failures and reports problems in its
    /// <c>error</c> array, so the status code is not consulted. An error that
    /// is not a permission answer is reported as inconclusive rather than as
    /// "permission absent": a service outage, a rejected test order or a rate
    /// limit say nothing about what the key may do, and reporting one as a
    /// missing permission would tell the user to change a key setting that is
    /// already correct.
    /// </remarks>
    internal static KrakenPermissionAnswer InterpretResponse(string payload)
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
                return new KrakenPermissionAnswer(Granted: true, Denied: false, Array.Empty<string>());
            }

            var messages = new List<string>();
            var denied = false;

            foreach (var error in errors.EnumerateArray())
            {
                var text = error.GetString();
                if (text is null)
                {
                    continue;
                }

                messages.Add(text);

                // An invalid key or signature is a credential problem rather
                // than a permission answer, and must not be reported as
                // "permission absent" because that would be misleading.
                if (text.Contains("Invalid key", StringComparison.Ordinal)
                    || text.Contains("Invalid signature", StringComparison.Ordinal))
                {
                    throw new ExchangePermissionProbeException(
                        "Kraken rejected this API key or its signature. Check that the API key and the private " +
                        "key are both copied in full and come from the same key pair.");
                }

                // A nonce problem says nothing about the key. Reporting it as
                // a bad key would send the user to re-copy a credential that is
                // correct.
                if (text.Contains("Invalid nonce", StringComparison.Ordinal))
                {
                    throw new ExchangePermissionProbeException(
                        "Kraken rejected the request because of its nonce, not because of the key. This happens " +
                        "when the same key has already been used with a higher nonce, for example by another " +
                        "application or an earlier version of this one. Wait a moment and try again, or create a " +
                        "new API key for this platform.");
                }

                // A key restricted to other IP addresses is a configuration
                // problem the user can fix, and is not a missing permission.
                if (text.Contains("Invalid IP", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("IP address", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ExchangePermissionProbeException(
                        "Kraken refused the request because of this key's IP address restriction. Add the address " +
                        "this application calls from to the key's allowed list, or turn the restriction off.");
                }

                if (text.Contains(PermissionDeniedError, StringComparison.Ordinal))
                {
                    denied = true;
                }
            }

            // Only an explicit denial is a "no". Anything else is left
            // inconclusive for the caller to interpret, because an error that
            // is not about permissions says nothing about permissions.
            return new KrakenPermissionAnswer(Granted: false, Denied: denied, messages);
        }
    }
}
