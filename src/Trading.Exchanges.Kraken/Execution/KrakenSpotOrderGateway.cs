using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Exchanges.Kraken.Execution;

/// <summary>
/// Places, cancels, queries and reads fills for Kraken Spot orders.
/// </summary>
/// <remarks>
/// <para>
/// This type can reach a real exchange with a real credential. It is
/// deliberately <b>not</b> registered in the application's dependency
/// injection container and does not implement
/// <c>ILiveExecutionRoute</c>, so its existence does not make live trading
/// reachable. The remaining Phase 9 tasks — the execution adapter, the replay
/// harness, the reconciliation taxonomy and the proving ceiling — must be in
/// place before anything is allowed to call it with a live credential.
/// </para>
/// <para>
/// Every order carries the platform's own client order id in Kraken's
/// <c>cl_ord_id</c> field. Kraken rejects a reused value, which turns
/// idempotency into something the exchange enforces rather than something this
/// code hopes for, and makes an order findable after a timeout when Kraken's
/// own identifier is exactly what never arrived.
/// </para>
/// <para>
/// There is no withdrawal, transfer or staking call here, and no method that
/// could be extended into one.
/// </para>
/// </remarks>
public sealed class KrakenSpotOrderGateway : ISpotOrderGateway
{
    internal const string AddOrderPath = "/0/private/AddOrder";
    internal const string CancelOrderPath = "/0/private/CancelOrder";
    internal const string QueryOrdersPath = "/0/private/QueryOrders";
    internal const string TradesHistoryPath = "/0/private/TradesHistory";

    private readonly HttpClient _httpClient;
    private readonly IKrakenNonceSource _nonceSource;

    public KrakenSpotOrderGateway(
        HttpClient httpClient,
        TimeProvider timeProvider,
        IKrakenNonceSource? nonceSource = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(timeProvider);

        _nonceSource = nonceSource ?? new KrakenNonceSource(timeProvider);
    }

    public ExchangeKind Exchange => ExchangeKind.Kraken;

    /// <summary>
    /// Builds the AddOrder body. Exposed to tests so the encoding of an order
    /// can be asserted without contacting Kraken.
    /// </summary>
    /// <remarks>
    /// All numbers are formatted with the invariant culture. A machine
    /// configured with a comma decimal separator would otherwise send
    /// <c>0,5</c>, which Kraken reads as a different quantity or rejects
    /// outright.
    /// </remarks>
    internal static string BuildAddOrderBody(string nonce, SpotOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var side = request.Side == SpotOrderSide.Buy ? "buy" : "sell";
        var type = request.Type == SpotOrderType.Limit ? "limit" : "market";

        var body = string.Create(
            CultureInfo.InvariantCulture,
            $"nonce={nonce}&ordertype={type}&type={side}&pair={Uri.EscapeDataString(request.Symbol)}" +
            $"&volume={request.Quantity}&cl_ord_id={Uri.EscapeDataString(request.ClientOrderId)}");

        if (request.LimitPrice is { } price)
        {
            body = string.Create(CultureInfo.InvariantCulture, $"{body}&price={price}");
        }

        // The flag is appended only when the caller asked for a dry run. It is
        // never removed by any other code path, and the request object cannot
        // express "validate" and "place" at the same time.
        if (request.ValidateOnly)
        {
            body += "&validate=true";
        }

        return body;
    }

    public async Task<SpotOrderPlacement> PlaceAsync(
        ExchangeCredential credential,
        SpotOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);

        KrakenResponse response;
        try
        {
            response = await SendAsync(
                credential,
                AddOrderPath,
                nonce => BuildAddOrderBody(nonce, request),
                cancellationToken).ConfigureAwait(false);
        }
        catch (KrakenTransportException exception)
        {
            // The request may have reached Kraken and may have been filled.
            // Nothing here proves otherwise, so the only safe answer is that
            // the outcome is unknown.
            return SpotOrderPlacement.Indeterminate(request.ClientOrderId, [exception.Message]);
        }

        if (response.Errors.Count > 0)
        {
            return KrakenSpotErrorClassifier.Classify(response.Errors) switch
            {
                KrakenErrorClass.BadRequest => SpotOrderPlacement.Rejected(request.ClientOrderId, response.Errors),
                KrakenErrorClass.Duplicate => SpotOrderPlacement.Duplicate(request.ClientOrderId, response.Errors),

                // A refused credential means Kraken did not act, but it is
                // reported as indeterminate rather than rejected because the
                // caller must not read it as a statement about the order.
                _ => SpotOrderPlacement.Indeterminate(request.ClientOrderId, response.Errors)
            };
        }

        var description = ReadDescription(response.Result);

        if (request.ValidateOnly)
        {
            // Kraken answers a validate-only request with a description and no
            // transaction id. Reporting this as an accepted order would invent
            // exposure that does not exist.
            return SpotOrderPlacement.Validated(request.ClientOrderId, description);
        }

        return SpotOrderPlacement.Accepted(
            request.ClientOrderId,
            ReadFirstTransactionId(response.Result),
            description);
    }

    public async Task<SpotOrderCancellation> CancelAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        var id = clientOrderId.Trim();

        KrakenResponse response;
        try
        {
            response = await SendAsync(
                credential,
                CancelOrderPath,
                nonce => $"nonce={nonce}&cl_ord_id={Uri.EscapeDataString(id)}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (KrakenTransportException exception)
        {
            return SpotOrderCancellation.Indeterminate([exception.Message]);
        }

        if (response.Errors.Count > 0)
        {
            return KrakenSpotErrorClassifier.Classify(response.Errors) switch
            {
                KrakenErrorClass.NotFound => SpotOrderCancellation.NotFound(response.Errors),
                KrakenErrorClass.AlreadyClosed => SpotOrderCancellation.AlreadyClosed(response.Errors),
                _ => SpotOrderCancellation.Indeterminate(response.Errors)
            };
        }

        // Kraken reports how many orders it cancelled. A count of zero means it
        // found nothing to cancel, which is not the same as having cancelled
        // something.
        if (response.Result.HasValue
            && response.Result.Value.TryGetProperty("count", out var count)
            && count.TryGetInt32(out var cancelled)
            && cancelled == 0)
        {
            return SpotOrderCancellation.NotFound([]);
        }

        return SpotOrderCancellation.Cancelled();
    }

    public async Task<OrderStatusQueryResult> QueryAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        var id = clientOrderId.Trim();

        KrakenResponse response;
        try
        {
            response = await SendAsync(
                credential,
                QueryOrdersPath,
                nonce => $"nonce={nonce}&trades=true&cl_ord_id={Uri.EscapeDataString(id)}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (KrakenTransportException exception)
        {
            return OrderStatusQueryResult.Unavailable(exception.Message);
        }

        if (response.Errors.Count > 0)
        {
            return KrakenSpotErrorClassifier.Classify(response.Errors) == KrakenErrorClass.NotFound
                ? OrderStatusQueryResult.NotFound()
                : OrderStatusQueryResult.Unavailable(
                    "Kraken did not report this order's state. It answered: "
                    + string.Join(", ", response.Errors));
        }

        if (!response.Result.HasValue || response.Result.Value.ValueKind != JsonValueKind.Object)
        {
            return OrderStatusQueryResult.Unavailable("Kraken returned no order details.");
        }

        foreach (var entry in response.Result.Value.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var state = MapOrderState(ReadString(entry.Value, "status"));
            if (state == ExchangeOrderState.Unknown)
            {
                // Kraken named a status this code does not recognise. Guessing
                // at it could report an open order as finished.
                return OrderStatusQueryResult.Unavailable(
                    "Kraken reported an order status this connector does not recognise.");
            }

            return OrderStatusQueryResult.Found(
                state,
                entry.Name,
                ReadDecimal(entry.Value, "vol_exec"));
        }

        // Kraken answered with an empty set, which is how it says it holds no
        // such order.
        return OrderStatusQueryResult.NotFound();
    }

    public async Task<SpotFillQueryResult> ListFillsAsync(
        ExchangeCredential credential,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var since = sinceUtc.ToUniversalTime().ToUnixTimeSeconds();

        KrakenResponse response;
        try
        {
            response = await SendAsync(
                credential,
                TradesHistoryPath,
                nonce => string.Create(CultureInfo.InvariantCulture, $"nonce={nonce}&type=all&start={since}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (KrakenTransportException exception)
        {
            return SpotFillQueryResult.Unavailable(exception.Message);
        }

        if (response.Errors.Count > 0)
        {
            return SpotFillQueryResult.Unavailable(
                "Kraken did not return trade history. It answered: " + string.Join(", ", response.Errors));
        }

        if (!response.Result.HasValue
            || !response.Result.Value.TryGetProperty("trades", out var trades)
            || trades.ValueKind != JsonValueKind.Object)
        {
            return SpotFillQueryResult.Unavailable("Kraken returned no trade collection.");
        }

        var fills = new List<SpotFill>();
        foreach (var trade in trades.EnumerateObject())
        {
            if (trade.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var quantity = ReadDecimal(trade.Value, "vol");
            if (quantity <= 0m)
            {
                continue;
            }

            fills.Add(new SpotFill(
                trade.Name,
                ReadString(trade.Value, "ordertxid") ?? trade.Name,
                ReadString(trade.Value, "pair") ?? "unknown",
                string.Equals(ReadString(trade.Value, "type"), "sell", StringComparison.Ordinal)
                    ? SpotOrderSide.Sell
                    : SpotOrderSide.Buy,
                quantity,
                ReadDecimal(trade.Value, "price"),
                ReadDecimal(trade.Value, "fee"),
                // Kraken does not name the fee's currency on a trade.
                feeCurrency: null,
                ReadUnixTime(trade.Value, "time")));
        }

        return SpotFillQueryResult.Answered(fills);
    }

    /// <summary>
    /// Maps Kraken's order status vocabulary. Anything unrecognised becomes
    /// <see cref="ExchangeOrderState.Unknown"/> so the caller treats it as an
    /// unanswered question rather than as a finished order.
    /// </summary>
    internal static ExchangeOrderState MapOrderState(string? status) => status switch
    {
        "pending" => ExchangeOrderState.PendingNew,
        "open" => ExchangeOrderState.New,
        "closed" => ExchangeOrderState.Filled,
        "canceled" => ExchangeOrderState.Canceled,
        "cancelled" => ExchangeOrderState.Canceled,
        "expired" => ExchangeOrderState.Expired,
        _ => ExchangeOrderState.Unknown
    };

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a monetary or quantity field as <see cref="decimal"/>.
    /// </summary>
    /// <remarks>
    /// Kraken sends these as strings. They are parsed with the invariant
    /// culture straight into decimal; they never pass through a binary floating
    /// point type, which cannot represent ordinary decimal prices exactly.
    /// </remarks>
    private static decimal ReadDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0m;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0m,
            JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : 0m,
            _ => 0m
        };
    }

    /// <summary>
    /// Reads Kraken's fractional Unix seconds as a UTC instant.
    /// </summary>
    /// <remarks>
    /// The value is read as decimal and scaled to milliseconds before
    /// conversion, so the sub-second part survives without going through a
    /// double.
    /// </remarks>
    private static DateTimeOffset ReadUnixTime(JsonElement element, string property)
    {
        var seconds = ReadDecimal(element, property);
        var milliseconds = decimal.ToInt64(decimal.Round(seconds * 1000m, 0, MidpointRounding.AwayFromZero));
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static string? ReadDescription(JsonElement? result) =>
        result.HasValue
        && result.Value.ValueKind == JsonValueKind.Object
        && result.Value.TryGetProperty("descr", out var descr)
        && descr.ValueKind == JsonValueKind.Object
            ? ReadString(descr, "order")
            : null;

    private static string? ReadFirstTransactionId(JsonElement? result)
    {
        if (!result.HasValue
            || result.Value.ValueKind != JsonValueKind.Object
            || !result.Value.TryGetProperty("txid", out var txid)
            || txid.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var id in txid.EnumerateArray())
        {
            if (id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }

        return null;
    }

    private async Task<KrakenResponse> SendAsync(
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
            throw new ExchangeCredentialFormatException(
                "The stored Kraken private key is not in the format Kraken issues, so the request could not be " +
                "signed. Reconnect the account with the private key exactly as Kraken shows it.",
                exception);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body)
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.TryAddWithoutValidation("API-Key", credential.ApiKey);
        request.Headers.TryAddWithoutValidation("API-Sign", signature);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // The message names no credential and no header value.
            throw new KrakenTransportException(
                "Kraken could not be reached, so the outcome of this request is unknown: " + exception.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KrakenTransportException(
                "The request to Kraken timed out, so the outcome of this request is unknown.");
        }

        using (httpResponse)
        {
            var payload = await httpResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(payload, httpResponse.IsSuccessStatusCode);
        }
    }

    /// <summary>
    /// Reads a Kraken envelope.
    /// </summary>
    /// <remarks>
    /// Kraken answers HTTP 200 with its failures listed in an <c>error</c>
    /// array, so the status code alone decides nothing. A body that cannot be
    /// read is raised as a transport problem, because an unreadable answer
    /// establishes nothing about the order.
    /// </remarks>
    internal static KrakenResponse Parse(string payload, bool httpSucceeded)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new KrakenTransportException(
                httpSucceeded
                    ? "Kraken returned an empty response, so the outcome of this request is unknown."
                    : "Kraken returned an error with no body, so the outcome of this request is unknown.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw new KrakenTransportException(
                "Kraken returned a response that could not be read, so the outcome of this request is unknown.");
        }

        using (document)
        {
            var errors = new List<string>();
            if (document.RootElement.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var error in errorElement.EnumerateArray())
                {
                    if (error.GetString() is { } text)
                    {
                        errors.Add(text);
                    }
                }
            }

            JsonElement? result = null;
            if (document.RootElement.TryGetProperty("result", out var resultElement))
            {
                // Cloned because the document is disposed on leaving this scope.
                result = resultElement.Clone();
            }

            if (errors.Count == 0 && result is null)
            {
                throw new KrakenTransportException(
                    "Kraken returned neither a result nor an error, so the outcome of this request is unknown.");
            }

            return new KrakenResponse(errors, result);
        }
    }

    /// <summary>A parsed Kraken envelope.</summary>
    internal sealed record KrakenResponse(IReadOnlyList<string> Errors, JsonElement? Result);
}

/// <summary>
/// Raised when Kraken could not be reached or did not answer intelligibly.
/// </summary>
/// <remarks>
/// This exception always means "nothing was established". It must never be
/// turned into a rejection, because a request that was not answered may still
/// have been executed. Implementations must keep credential material out of
/// the message.
/// </remarks>
public sealed class KrakenTransportException : Exception
{
    public KrakenTransportException()
    {
    }

    public KrakenTransportException(string message)
        : base(message)
    {
    }

    public KrakenTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
