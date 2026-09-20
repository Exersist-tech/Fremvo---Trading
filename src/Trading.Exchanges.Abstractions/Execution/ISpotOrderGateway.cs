namespace Trading.Exchanges.Abstractions.Execution;

/// <summary>
/// Which way a spot order goes.
/// </summary>
public enum SpotOrderSide
{
    Buy = 0,
    Sell = 1
}

/// <summary>
/// The order types the platform will submit on spot.
/// </summary>
/// <remarks>
/// Deliberately small. Every additional type is another set of exchange
/// filters, another rejection taxonomy and another way for an order to become
/// materially different from what the strategy asked for.
/// </remarks>
public enum SpotOrderType
{
    Limit = 0,
    Market = 1
}

/// <summary>
/// A request to place one spot order, expressed without reference to any
/// particular exchange.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidateOnly"/> defaults to <see langword="true"/>. A caller that
/// forgets to state its intent therefore asks the exchange to check an order
/// rather than to place one. The safe value is the default because the unsafe
/// value spends real money.
/// </para>
/// <para>
/// <see cref="ClientOrderId"/> is mandatory and is the platform's own
/// identifier. It is what makes a submission idempotent and what makes an order
/// findable after a timeout, when the exchange's own identifier is precisely
/// the thing that never arrived.
/// </para>
/// </remarks>
public sealed class SpotOrderRequest
{
    public SpotOrderRequest(
        string symbol,
        SpotOrderSide side,
        SpotOrderType type,
        decimal quantity,
        decimal? limitPrice,
        string clientOrderId,
        bool validateOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        // A limit order without a price is not a limit order. Filling in a
        // default here would turn the strategy's instruction into a materially
        // different order, so it is refused instead.
        if (type == SpotOrderType.Limit && limitPrice is null)
        {
            throw new ArgumentNullException(nameof(limitPrice), "A limit order requires a limit price.");
        }

        if (limitPrice is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(limitPrice), "Limit price must be positive.");
        }

        // A price on a market order would be silently ignored by the exchange
        // while suggesting to the reader that it was honoured.
        if (type == SpotOrderType.Market && limitPrice is not null)
        {
            throw new ArgumentException("A market order must not carry a limit price.", nameof(limitPrice));
        }

        Symbol = symbol.Trim();
        Side = side;
        Type = type;
        Quantity = quantity;
        LimitPrice = limitPrice;
        ClientOrderId = clientOrderId.Trim();
        ValidateOnly = validateOnly;
    }

    public string Symbol { get; }

    public SpotOrderSide Side { get; }

    public SpotOrderType Type { get; }

    public decimal Quantity { get; }

    public decimal? LimitPrice { get; }

    public string ClientOrderId { get; }

    /// <summary>
    /// When true the exchange checks the order and does not place it.
    /// </summary>
    public bool ValidateOnly { get; }
}

/// <summary>
/// What became of a placement attempt.
/// </summary>
public enum SpotPlacementOutcome
{
    /// <summary>
    /// The exchange accepted the order. Real exposure may now exist.
    /// </summary>
    Accepted = 0,

    /// <summary>
    /// The exchange checked a validate-only request and raised no objection.
    /// No order exists and no exposure was created. Kept apart from
    /// <see cref="Accepted"/> so a dry run can never be mistaken for a trade.
    /// </summary>
    Validated = 1,

    /// <summary>
    /// The exchange refused the order because of the order itself. The order
    /// does not exist and cannot come into existence from this request, so
    /// resubmitting a corrected order is safe.
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// The exchange already holds an order with this client order id. This is
    /// evidence that an earlier submission succeeded, so the correct response
    /// is to query that order, never to submit another.
    /// </summary>
    DuplicateClientOrderId = 3,

    /// <summary>
    /// Nothing was established. The order may or may not exist. It must be
    /// reconciled before any further submission for the same intent.
    /// </summary>
    Indeterminate = 4
}

/// <summary>
/// The result of a placement attempt.
/// </summary>
/// <remarks>
/// <see cref="SpotPlacementOutcome.Rejected"/> and
/// <see cref="SpotPlacementOutcome.Indeterminate"/> are separate and must stay
/// separate. Folding an unanswered request into "rejected" is how a timeout
/// becomes two live positions.
/// </remarks>
public sealed record SpotOrderPlacement
{
    private SpotOrderPlacement(
        SpotPlacementOutcome outcome,
        string clientOrderId,
        string? exchangeOrderId,
        string? description,
        IReadOnlyList<string> exchangeErrors)
    {
        Outcome = outcome;
        ClientOrderId = clientOrderId;
        ExchangeOrderId = exchangeOrderId;
        Description = description;
        ExchangeErrors = exchangeErrors;
    }

    public SpotPlacementOutcome Outcome { get; }

    public string ClientOrderId { get; }

    /// <summary>
    /// The exchange's own identifier. Null for every outcome except
    /// <see cref="SpotPlacementOutcome.Accepted"/>, and null even there when
    /// the exchange accepted without returning one.
    /// </summary>
    public string? ExchangeOrderId { get; }

    /// <summary>The exchange's human-readable echo of the order, when given.</summary>
    public string? Description { get; }

    /// <summary>
    /// The exchange's own error codes. These are fixed strings chosen by the
    /// exchange and carry no credential material.
    /// </summary>
    public IReadOnlyList<string> ExchangeErrors { get; }

    public static SpotOrderPlacement Accepted(
        string clientOrderId,
        string? exchangeOrderId,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        return new SpotOrderPlacement(
            SpotPlacementOutcome.Accepted, clientOrderId.Trim(), exchangeOrderId, description, []);
    }

    public static SpotOrderPlacement Validated(string clientOrderId, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        return new SpotOrderPlacement(
            SpotPlacementOutcome.Validated, clientOrderId.Trim(), exchangeOrderId: null, description, []);
    }

    public static SpotOrderPlacement Rejected(string clientOrderId, IReadOnlyList<string> exchangeErrors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentNullException.ThrowIfNull(exchangeErrors);

        if (exchangeErrors.Count == 0)
        {
            throw new ArgumentException(
                "A rejection must carry the exchange's reason, otherwise it cannot be distinguished from an " +
                "unanswered request.",
                nameof(exchangeErrors));
        }

        return new SpotOrderPlacement(
            SpotPlacementOutcome.Rejected, clientOrderId.Trim(), null, null, exchangeErrors);
    }

    public static SpotOrderPlacement Duplicate(string clientOrderId, IReadOnlyList<string> exchangeErrors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentNullException.ThrowIfNull(exchangeErrors);

        return new SpotOrderPlacement(
            SpotPlacementOutcome.DuplicateClientOrderId, clientOrderId.Trim(), null, null, exchangeErrors);
    }

    public static SpotOrderPlacement Indeterminate(string clientOrderId, IReadOnlyList<string> exchangeErrors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentNullException.ThrowIfNull(exchangeErrors);

        return new SpotOrderPlacement(
            SpotPlacementOutcome.Indeterminate, clientOrderId.Trim(), null, null, exchangeErrors);
    }
}

/// <summary>
/// What became of a cancellation attempt.
/// </summary>
public enum SpotCancellationOutcome
{
    /// <summary>The exchange confirmed the order is cancelled.</summary>
    Cancelled = 0,

    /// <summary>
    /// The exchange does not know this order. It cannot be resting, so there is
    /// nothing to cancel.
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// The order had already reached a terminal state, so cancellation had
    /// nothing to act on. This is not a failure, but it does mean any fill
    /// already happened.
    /// </summary>
    AlreadyClosed = 2,

    /// <summary>
    /// Nothing was established. The order may still be live and must be
    /// reconciled.
    /// </summary>
    Indeterminate = 3
}

/// <summary>The result of a cancellation attempt.</summary>
public sealed record SpotOrderCancellation(
    SpotCancellationOutcome Outcome,
    IReadOnlyList<string> ExchangeErrors)
{
    public static SpotOrderCancellation Cancelled() => new(SpotCancellationOutcome.Cancelled, []);

    public static SpotOrderCancellation NotFound(IReadOnlyList<string> exchangeErrors) =>
        new(SpotCancellationOutcome.NotFound, exchangeErrors ?? []);

    public static SpotOrderCancellation AlreadyClosed(IReadOnlyList<string> exchangeErrors) =>
        new(SpotCancellationOutcome.AlreadyClosed, exchangeErrors ?? []);

    public static SpotOrderCancellation Indeterminate(IReadOnlyList<string> exchangeErrors) =>
        new(SpotCancellationOutcome.Indeterminate, exchangeErrors ?? []);
}

/// <summary>
/// One execution against an order, as the exchange reports it.
/// </summary>
/// <remarks>
/// Every monetary field is a <see cref="decimal"/> and the timestamp is UTC.
/// </remarks>
public sealed record SpotFill
{
    public SpotFill(
        string fillId,
        string exchangeOrderId,
        string symbol,
        SpotOrderSide side,
        decimal quantity,
        decimal price,
        decimal fee,
        string? feeCurrency,
        DateTimeOffset executedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchangeOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "A fill must have a positive quantity.");
        }

        if (price < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "A fill price cannot be negative.");
        }

        if (fee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fee), "A fee cannot be negative.");
        }

        FillId = fillId.Trim();
        ExchangeOrderId = exchangeOrderId.Trim();
        Symbol = symbol.Trim();
        Side = side;
        Quantity = quantity;
        Price = price;
        Fee = fee;
        FeeCurrency = string.IsNullOrWhiteSpace(feeCurrency) ? null : feeCurrency.Trim();
        ExecutedAtUtc = executedAtUtc.ToUniversalTime();
    }

    public string FillId { get; }

    public string ExchangeOrderId { get; }

    /// <summary>The exchange's own symbol for the pair, exactly as reported.</summary>
    public string Symbol { get; }

    public SpotOrderSide Side { get; }

    public decimal Quantity { get; }

    public decimal Price { get; }

    public decimal Fee { get; }

    /// <summary>
    /// The asset the fee was charged in, or <see langword="null"/> when the
    /// exchange did not state it.
    /// </summary>
    /// <remarks>
    /// Kraken's trade history reports a fee amount without naming its currency.
    /// The value is left null rather than assumed to be the quote asset,
    /// because an assumed fee currency silently misstates the cost of a trade
    /// and would flow straight into profit and loss. <see cref="Symbol"/> is
    /// carried so the instrument catalogue, which does know the quote asset,
    /// can resolve it.
    /// </remarks>
    public string? FeeCurrency { get; }

    public DateTimeOffset ExecutedAtUtc { get; }
}

/// <summary>What a fill query established.</summary>
public enum SpotFillQueryOutcome
{
    /// <summary>
    /// The exchange answered. The list is complete for the window asked about,
    /// and an empty list means there were no fills.
    /// </summary>
    Answered = 0,

    /// <summary>
    /// The exchange did not answer. An empty list here means nothing at all and
    /// must never be read as "no fills".
    /// </summary>
    Unavailable = 1
}

/// <summary>The result of a fill query.</summary>
public sealed record SpotFillQueryResult(
    SpotFillQueryOutcome Outcome,
    IReadOnlyList<SpotFill> Fills,
    string? FailureReason)
{
    public static SpotFillQueryResult Answered(IReadOnlyList<SpotFill> fills)
    {
        ArgumentNullException.ThrowIfNull(fills);
        return new SpotFillQueryResult(SpotFillQueryOutcome.Answered, fills, null);
    }

    public static SpotFillQueryResult Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SpotFillQueryResult(SpotFillQueryOutcome.Unavailable, [], reason.Trim());
    }
}

/// <summary>
/// The operations the platform performs against an exchange's spot trading API.
/// </summary>
/// <remarks>
/// <para>
/// There is no withdrawal, transfer or funding operation on this interface, and
/// there is no plan for one. The platform never moves user funds, so the
/// capability does not exist to be called by mistake.
/// </para>
/// <para>
/// There is also no "retry" operation. Re-sending after an unanswered request
/// is a decision that belongs to reconciliation, which can first establish what
/// the exchange actually did.
/// </para>
/// <para>
/// Spot is a capability in its own right. Leverage is not a flag that will be
/// added to these types later; margin and futures get their own gateways.
/// </para>
/// </remarks>
public interface ISpotOrderGateway
{
    /// <summary>The exchange this gateway talks to.</summary>
    ExchangeKind Exchange { get; }

    /// <summary>
    /// Places, or validates, a single order.
    /// </summary>
    Task<SpotOrderPlacement> PlaceAsync(
        ExchangeCredential credential,
        SpotOrderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an order identified by the platform's own client order id.
    /// </summary>
    Task<SpotOrderCancellation> CancelAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the exchange what actually happened to an order, keyed on the
    /// client order id so the answer is reachable even when the submission
    /// response was lost.
    /// </summary>
    Task<OrderStatusQueryResult> QueryAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists fills executed at or after <paramref name="sinceUtc"/>.
    /// </summary>
    Task<SpotFillQueryResult> ListFillsAsync(
        ExchangeCredential credential,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default);
}
