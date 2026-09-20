namespace Trading.Exchanges.Abstractions.Execution;

/// <summary>Direction of a futures order at the venue.</summary>
public enum FuturesOrderSide
{
    Buy = 0,
    Sell = 1
}

/// <summary>The position an order opens, or the position a reduce-only order closes.</summary>
public enum FuturesPositionDirection
{
    LongPosition = 0,
    ShortPosition = 1
}

/// <summary>The deliberately small set of futures order types supported by this port.</summary>
public enum FuturesOrderType
{
    Limit = 0,
    Market = 1
}

/// <summary>A normalized futures order request.</summary>
/// <remarks>
/// Futures are a separate capability from spot. In particular, reduce-only is
/// explicit and defaults to false rather than being inferred from the side.
/// A gateway may impose a stricter safety policy on non-reduce-only requests.
/// </remarks>
public sealed class FuturesOrderRequest
{
    public FuturesOrderRequest(
        string symbol,
        FuturesOrderSide side,
        FuturesPositionDirection positionDirection,
        FuturesOrderType type,
        decimal quantity,
        decimal? limitPrice,
        bool reduceOnly,
        string clientOrderId,
        bool validateOnly = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (type == FuturesOrderType.Limit && limitPrice is null)
        {
            throw new ArgumentNullException(nameof(limitPrice), "A limit order requires a limit price.");
        }

        if (limitPrice is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(limitPrice), "Limit price must be positive.");
        }

        if (type == FuturesOrderType.Market && limitPrice is not null)
        {
            throw new ArgumentException("A market order must not carry a limit price.", nameof(limitPrice));
        }

        Symbol = symbol.Trim();
        Side = side;
        PositionDirection = positionDirection;
        Type = type;
        Quantity = quantity;
        LimitPrice = limitPrice;
        ReduceOnly = reduceOnly;
        ClientOrderId = clientOrderId.Trim();
        ValidateOnly = validateOnly;
    }

    public string Symbol { get; }
    public FuturesOrderSide Side { get; }
    public FuturesPositionDirection PositionDirection { get; }
    public FuturesOrderType Type { get; }
    public decimal Quantity { get; }
    public decimal? LimitPrice { get; }
    public bool ReduceOnly { get; }
    public string ClientOrderId { get; }
    public bool ValidateOnly { get; }
}

/// <summary>What a futures placement attempt established.</summary>
public enum FuturesPlacementOutcome
{
    Accepted = 0,
    Validated = 1,
    Rejected = 2,
    DuplicateClientOrderId = 3,
    Indeterminate = 4
}

/// <summary>Result of a futures order placement.</summary>
public sealed record FuturesOrderPlacement(
    FuturesPlacementOutcome Outcome,
    string ClientOrderId,
    string? ExchangeOrderId,
    IReadOnlyList<string> ExchangeErrors)
{
    public static FuturesOrderPlacement Create(
        FuturesPlacementOutcome outcome,
        string clientOrderId,
        string? exchangeOrderId = null,
        IReadOnlyList<string>? errors = null) =>
        new(outcome, clientOrderId, exchangeOrderId, errors ?? []);
}

/// <summary>What a futures cancellation attempt established.</summary>
public enum FuturesCancellationOutcome
{
    Cancelled = 0,
    NotFound = 1,
    AlreadyClosed = 2,
    Indeterminate = 3
}

/// <summary>Result of a futures cancellation.</summary>
public sealed record FuturesOrderCancellation(
    FuturesCancellationOutcome Outcome,
    IReadOnlyList<string> ExchangeErrors);

/// <summary>Normalized state returned for an open futures order.</summary>
public sealed record FuturesOrderState(
    string ClientOrderId,
    string ExchangeOrderId,
    FuturesOrderSide Side,
    decimal FilledQuantity,
    decimal UnfilledQuantity,
    bool ReduceOnly,
    DateTimeOffset LastUpdatedUtc);

/// <summary>Result of querying the futures venue by client order id.</summary>
public enum FuturesOrderQueryOutcome
{
    Found = 0,
    Unavailable = 1
}

/// <summary>
/// Futures order capability. It intentionally contains no account, funding,
/// transfer, withdrawal, deposit, leverage or retry operation.
/// </summary>
public interface IFuturesOrderGateway
{
    ExchangeKind Exchange { get; }

    Task<FuturesOrderPlacement> PlaceAsync(
        ExchangeCredential credential,
        FuturesOrderRequest request,
        CancellationToken cancellationToken = default);

    Task<FuturesOrderCancellation> CancelAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries an open order. An absent open order does not prove it was never
    /// accepted (it may have filled or been cancelled), so it is unavailable
    /// rather than "not found".
    /// </summary>
    Task<(FuturesOrderQueryOutcome Outcome, FuturesOrderState? Order, string? FailureReason)> QueryAsync(
        ExchangeCredential credential,
        string clientOrderId,
        CancellationToken cancellationToken = default);
}
