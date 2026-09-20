using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Exchanges.Kraken.Execution;

/// <summary>Configuration for the Kraken Futures demo API only.</summary>
public sealed class KrakenFuturesDemoOptions
{
    public KrakenFuturesDemoOptions(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (baseUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(baseUri.Host, "demo-futures.kraken.com", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(baseUri.AbsolutePath.TrimEnd('/'), "/derivatives/api/v3", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Only https://demo-futures.kraken.com/derivatives/api/v3 is permitted.", nameof(baseUri));
        }

        BaseUri = baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);
    }

    public Uri BaseUri { get; }
}

/// <summary>
/// An explicit opt-in marker for replay tests. It is never registered by the
/// application and is required even for demo API calls.
/// </summary>
public sealed class KrakenFuturesDemoRoute
{
    private KrakenFuturesDemoRoute()
    {
    }

    public static KrakenFuturesDemoRoute EnableForReplayTests() => new();
}

/// <summary>
/// Kraken Futures connector constrained to the demo host and three order
/// routes: send, cancel, and open-order query.
/// </summary>
public sealed class KrakenFuturesDemoOrderGateway : IFuturesOrderGateway
{
    internal const string SendOrderPath = "sendorder";
    internal const string CancelOrderPath = "cancelorder";
    internal const string OpenOrdersPath = "openorders";

    private readonly HttpClient _httpClient;
    private readonly Uri _demoBaseUri;
    private readonly bool _isEnabled;

    public KrakenFuturesDemoOrderGateway(
        HttpClient httpClient,
        KrakenFuturesDemoOptions options,
        KrakenFuturesDemoRoute? replayTestRoute = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        _demoBaseUri = options.BaseUri;
        _isEnabled = replayTestRoute is not null;
    }

    public ExchangeKind Exchange => ExchangeKind.Kraken;

    internal static string BuildSendOrderBody(FuturesOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var side = request.Side == FuturesOrderSide.Buy ? "buy" : "sell";
        var orderType = request.Type == FuturesOrderType.Limit ? "lmt" : "mkt";
        var body = string.Create(
            CultureInfo.InvariantCulture,
            $"orderType={orderType}&symbol={Uri.EscapeDataString(request.Symbol)}&side={side}" +
            $"&size={request.Quantity}&cliOrdId={Uri.EscapeDataString(request.ClientOrderId)}" +
            $"&reduceOnly={(request.ReduceOnly ? "true" : "false")}&validate={(request.ValidateOnly ? "true" : "false")}");
        return request.LimitPrice is { } price
            ? string.Create(CultureInfo.InvariantCulture, $"{body}&limitPrice={price}")
            : body;
    }

    internal static bool IsValidClientOrderId(string? value) =>
        value is { Length: > 0 and <= 100 } && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    public async Task<FuturesOrderPlacement> PlaceAsync(
        ExchangeCredential credential,
        FuturesOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);
        if (!_isEnabled)
        {
            return Rejected(request.ClientOrderId, "The Kraken Futures demo gateway is disabled without an explicit replay-test route.");
        }

        if (!IsValidClientOrderId(request.ClientOrderId))
        {
            return Rejected(request.ClientOrderId, "Invalid client order id. It must contain only letters, digits, hyphens, or underscores and be at most 100 characters.");
        }

        if (request.ReduceOnly
            && ((request.PositionDirection == FuturesPositionDirection.LongPosition && request.Side != FuturesOrderSide.Sell)
                || (request.PositionDirection == FuturesPositionDirection.ShortPosition && request.Side != FuturesOrderSide.Buy)))
        {
            return Rejected(request.ClientOrderId, "A reduce-only order's side must reduce the stated position direction.");
        }

        // The connector deliberately never creates demo exposure. A caller may
        // validate an opening order, but submission must be reduce-only.
        if (!request.ValidateOnly && !request.ReduceOnly)
        {
            return Rejected(request.ClientOrderId, "Position-increasing futures orders are unsupported by this demo gateway.");
        }

        JsonDocument response;
        try
        {
            response = await SendAsync(credential, HttpMethod.Post, SendOrderPath, BuildSendOrderBody(request), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return FuturesOrderPlacement.Create(FuturesPlacementOutcome.Indeterminate, request.ClientOrderId, errors: ["The demo venue did not answer."]);
        }

        using (response)
        {
            var errors = ReadErrors(response.RootElement);
            if (errors.Count > 0)
            {
                var outcome = errors.Any(error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                                                   || error.Contains("cliOrdId", StringComparison.OrdinalIgnoreCase))
                    ? FuturesPlacementOutcome.DuplicateClientOrderId
                    : IsDefiniteOrderRefusal(errors) ? FuturesPlacementOutcome.Rejected : FuturesPlacementOutcome.Indeterminate;
                return FuturesOrderPlacement.Create(outcome, request.ClientOrderId, errors: errors);
            }

            if (!IsSuccess(response.RootElement))
            {
                return FuturesOrderPlacement.Create(FuturesPlacementOutcome.Indeterminate, request.ClientOrderId, errors: ["The demo venue returned an unrecognised response."]);
            }

            if (request.ValidateOnly)
            {
                return FuturesOrderPlacement.Create(FuturesPlacementOutcome.Validated, request.ClientOrderId);
            }

            var exchangeId = TryGetString(response.RootElement, "sendStatus", "order_id");
            return FuturesOrderPlacement.Create(FuturesPlacementOutcome.Accepted, request.ClientOrderId, exchangeId);
        }
    }

    public async Task<FuturesOrderCancellation> CancelAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        if (!_isEnabled)
        {
            return new(FuturesCancellationOutcome.Indeterminate, ["The Kraken Futures demo gateway is disabled without an explicit replay-test route."]);
        }

        if (!IsValidClientOrderId(clientOrderId))
        {
            return new(FuturesCancellationOutcome.Indeterminate, ["Invalid client order id."]);
        }

        try
        {
            using var response = await SendAsync(credential, HttpMethod.Post, CancelOrderPath, $"cliOrdId={Uri.EscapeDataString(clientOrderId.Trim())}", cancellationToken).ConfigureAwait(false);
            var errors = ReadErrors(response.RootElement);
            if (errors.Count > 0)
            {
                return new(FuturesCancellationOutcome.Indeterminate, errors);
            }

            var status = TryGetString(response.RootElement, "cancelStatus", "status");
            return status switch
            {
                _ when string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase) => new(FuturesCancellationOutcome.Cancelled, []),
                _ when string.Equals(status, "filled", StringComparison.OrdinalIgnoreCase) => new(FuturesCancellationOutcome.AlreadyClosed, []),
                _ when string.Equals(status, "notFound", StringComparison.OrdinalIgnoreCase) => new(FuturesCancellationOutcome.NotFound, []),
                _ => new(FuturesCancellationOutcome.Indeterminate, ["The demo venue returned an unrecognised cancellation status."])
            };
        }
        catch (HttpRequestException)
        {
            return new(FuturesCancellationOutcome.Indeterminate, ["The demo venue did not answer."]);
        }
    }

    public async Task<(FuturesOrderQueryOutcome Outcome, FuturesOrderState? Order, string? FailureReason)> QueryAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        if (!_isEnabled)
        {
            return (FuturesOrderQueryOutcome.Unavailable, null, "The Kraken Futures demo gateway is disabled without an explicit replay-test route.");
        }

        try
        {
            using var response = await SendAsync(credential, HttpMethod.Get, OpenOrdersPath, null, cancellationToken).ConfigureAwait(false);
            var errors = ReadErrors(response.RootElement);
            if (errors.Count > 0 || !IsSuccess(response.RootElement))
            {
                return (FuturesOrderQueryOutcome.Unavailable, null, "The demo venue did not establish this order's state.");
            }

            if (!response.RootElement.TryGetProperty("openOrders", out var orders) || orders.ValueKind != JsonValueKind.Array)
            {
                return (FuturesOrderQueryOutcome.Unavailable, null, "The demo venue returned no open-order list.");
            }

            foreach (var order in orders.EnumerateArray())
            {
                if (string.Equals(TryGetString(order, "cliOrdId"), clientOrderId.Trim(), StringComparison.Ordinal))
                {
                    var updated = TryGetDateTimeOffset(order, "lastUpdateTime") ?? DateTimeOffset.UnixEpoch;
                    return (FuturesOrderQueryOutcome.Found, new FuturesOrderState(
                        clientOrderId.Trim(),
                        TryGetString(order, "order_id") ?? string.Empty,
                        string.Equals(TryGetString(order, "side"), "sell", StringComparison.OrdinalIgnoreCase) ? FuturesOrderSide.Sell : FuturesOrderSide.Buy,
                        TryGetDecimal(order, "filledSize"),
                        TryGetDecimal(order, "unfilledSize"),
                        order.TryGetProperty("reduceOnly", out var reduceOnly) && reduceOnly.ValueKind == JsonValueKind.True,
                        updated.ToUniversalTime()), null);
                }
            }

            return (FuturesOrderQueryOutcome.Unavailable, null, "No matching open order was returned; it may have filled or been cancelled.");
        }
        catch (HttpRequestException)
        {
            return (FuturesOrderQueryOutcome.Unavailable, null, "The demo venue did not answer.");
        }
    }

    private async Task<JsonDocument> SendAsync(ExchangeCredential credential, HttpMethod method, string relativePath, string? body, CancellationToken cancellationToken)
    {
        var path = "/derivatives/api/v3/" + relativePath;
        // An absolute, already-validated demo URI means a caller changing
        // HttpClient.BaseAddress cannot redirect this connector to production.
        using var request = new HttpRequestMessage(method, new Uri(_demoBaseUri, relativePath));
        request.Headers.Add("APIKey", credential.ApiKey);
        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        request.Headers.Add("Nonce", nonce);
        request.Headers.Add("Authent", Sign(path, body ?? string.Empty, nonce, credential.ApiSecret));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(payload);
    }

    internal static string Sign(string endpointPath, string postData, string nonce, string apiSecret)
    {
        byte[] secret;
        try { secret = Convert.FromBase64String(apiSecret); }
        catch (FormatException exception) { throw new ExchangeCredentialFormatException("The Kraken Futures API secret is not valid base64.", exception); }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(postData + nonce + endpointPath));
        using var hmac = new HMACSHA512(secret);
        return Convert.ToBase64String(hmac.ComputeHash(digest));
    }

    private static FuturesOrderPlacement Rejected(string id, string reason) =>
        FuturesOrderPlacement.Create(FuturesPlacementOutcome.Rejected, id, errors: [reason]);

    private static bool IsSuccess(JsonElement root) =>
        string.Equals(TryGetString(root, "result"), "success", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefiniteOrderRefusal(IReadOnlyList<string> errors) =>
        errors.All(error => error.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                            || error.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
                            || error.Contains("reduce", StringComparison.OrdinalIgnoreCase));

    private static List<string> ReadErrors(JsonElement root)
    {
        var errors = new List<string>();
        if (root.TryGetProperty("errors", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            errors.AddRange(many.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        else if (root.TryGetProperty("error", out var one) && one.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(one.GetString()))
        {
            errors.Add(one.GetString()!);
        }

        return errors;
    }

    private static string? TryGetString(JsonElement root, string property, string? child = null) =>
        root.TryGetProperty(property, out var value)
        && (child is null || value.TryGetProperty(child, out value))
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal TryGetDecimal(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetDecimal(out var number) ? number : 0m;

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp : null;
}
