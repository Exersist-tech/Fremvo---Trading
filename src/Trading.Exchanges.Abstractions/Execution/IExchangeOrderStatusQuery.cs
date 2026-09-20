namespace Trading.Exchanges.Abstractions.Execution;

/// <summary>
/// Exchange-neutral order state as reported by a connector.
/// </summary>
/// <remarks>
/// Declared here rather than reused from the domain so that connectors depend
/// only on this abstraction package and never on core trading types.
/// </remarks>
public enum ExchangeOrderState
{
    Unknown = 0,
    New,
    PendingNew,
    PartiallyFilled,
    Filled,
    Canceled,
    Rejected,
    Expired,
    Failed,
    PendingCancel,
    PendingReplace
}

/// <summary>
/// The outcome of asking an exchange what happened to an order.
/// </summary>
public enum OrderStatusQueryOutcome
{
    /// <summary>
    /// The exchange answered and knows the order.
    /// </summary>
    Found = 0,

    /// <summary>
    /// The exchange answered and does not know the order. This is positive
    /// proof that the order was never accepted.
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// The exchange could not be reached, or answered in a way that does not
    /// establish the order's state. Nothing has been proven.
    /// </summary>
    Unavailable = 2
}

/// <summary>
/// The result of a status query.
/// </summary>
/// <remarks>
/// <see cref="OrderStatusQueryOutcome.NotFound"/> and
/// <see cref="OrderStatusQueryOutcome.Unavailable"/> are kept strictly apart.
/// Collapsing them into a single "no order" answer is the classic way to turn
/// a network timeout into a duplicated live position.
/// </remarks>
public sealed record OrderStatusQueryResult
{
    private OrderStatusQueryResult(
        OrderStatusQueryOutcome outcome,
        ExchangeOrderState state,
        string? exchangeOrderId,
        decimal filledQuantity,
        string? failureReason)
    {
        Outcome = outcome;
        State = state;
        ExchangeOrderId = exchangeOrderId;
        FilledQuantity = filledQuantity;
        FailureReason = failureReason;
    }

    public OrderStatusQueryOutcome Outcome { get; }

    public ExchangeOrderState State { get; }

    public string? ExchangeOrderId { get; }

    public decimal FilledQuantity { get; }

    public string? FailureReason { get; }

    public static OrderStatusQueryResult Found(
        ExchangeOrderState state,
        string? exchangeOrderId,
        decimal filledQuantity)
    {
        if (state == ExchangeOrderState.Unknown)
        {
            throw new ArgumentException(
                "A found order must report a concrete state.", nameof(state));
        }

        if (filledQuantity < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(filledQuantity), "Filled quantity cannot be negative.");
        }

        return new OrderStatusQueryResult(
            OrderStatusQueryOutcome.Found, state, exchangeOrderId, filledQuantity, failureReason: null);
    }

    /// <summary>
    /// The exchange successfully reported that no such order exists, which
    /// proves the submission never reached the matching engine.
    /// </summary>
    public static OrderStatusQueryResult NotFound() =>
        new(OrderStatusQueryOutcome.NotFound, ExchangeOrderState.Rejected, null, 0m, null);

    /// <summary>
    /// The query failed. This proves nothing and must never be treated as an
    /// absent order.
    /// </summary>
    public static OrderStatusQueryResult Unavailable(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new OrderStatusQueryResult(
            OrderStatusQueryOutcome.Unavailable, ExchangeOrderState.Unknown, null, 0m, reason.Trim());
    }
}

/// <summary>
/// Queries an exchange for the true state of an order.
/// </summary>
/// <remarks>
/// The lookup is keyed on the client order id, which the platform always
/// knows, rather than the exchange order id, which is exactly the value that
/// is missing when a submission times out.
/// </remarks>
public interface IExchangeOrderStatusQuery
{
    Task<OrderStatusQueryResult> QueryByClientOrderIdAsync(
        string clientOrderId,
        string symbol,
        CancellationToken cancellationToken);
}
