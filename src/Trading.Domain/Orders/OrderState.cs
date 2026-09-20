using Trading.Domain.Execution;

namespace Trading.Domain.Orders;

public enum OrderState
{
    Draft = 0,
    PendingValidation,
    Accepted,
    Rejected,
    Canceled,
    New,
    PartiallyFilled,
    Filled,
    Expired,
    Failed,
    Replaced
}

public enum OrderSide
{
    Buy = 0,
    Sell = 1
}

public enum OrderType
{
    Market = 0,
    Limit,
    StopLoss,
    StopLimit,
    TakeProfit,
    TakeProfitLimit
}

public sealed class Order
{
    public Order(
        Guid id,
        Guid userId,
        Guid strategyId,
        string symbol,
        OrderSide side,
        OrderType type,
        decimal quantity,
        decimal price,
        DateTimeOffset createdAtUtc,
        string clientOrderId,
        bool reduceOnly = false,
        bool closeOnly = false,
        TradingMode mode = TradingMode.Paper)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (strategyId == Guid.Empty)
        {
            throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            throw new ArgumentException("Client order id is required.", nameof(clientOrderId));
        }

        Id = id;
        UserId = userId;
        StrategyId = strategyId;
        Symbol = symbol.Trim();
        Side = side;
        Type = type;
        Quantity = quantity;
        Price = price;
        CreatedAtUtc = createdAtUtc;
        ClientOrderId = clientOrderId.Trim();
        ReduceOnly = reduceOnly;
        CloseOnly = closeOnly;
        Mode = mode;
        State = OrderState.Draft;
        Version = 0;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public Guid StrategyId { get; }

    public string Symbol { get; }

    public OrderSide Side { get; }

    public OrderType Type { get; }

    public decimal Quantity { get; }

    public decimal Price { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string ClientOrderId { get; }

    public bool ReduceOnly { get; }

    public bool CloseOnly { get; }

    /// <summary>
    /// Which book this order belongs to. The default is
    /// <see cref="TradingMode.Paper"/> so an omitted argument produces a
    /// simulated order rather than one that claims to have reached a venue.
    /// </summary>
    public TradingMode Mode { get; }

    public OrderState State { get; private set; }

    public int Version { get; private set; }

    /// <summary>
    /// The exchange's own order identifier, once the exchange has confirmed
    /// one. Null while the order has never been acknowledged.
    /// </summary>
    public string? ExchangeOrderId { get; private set; }

    public decimal FilledQuantity { get; private set; }

    public decimal RemainingQuantity => Quantity - FilledQuantity;

    public DateTimeOffset? LastTransitionAtUtc { get; private set; }

    /// <summary>
    /// True when the exchange outcome is unknown and must be established
    /// before anything further is done with this order.
    /// </summary>
    /// <remarks>
    /// This is the single most important flag on the aggregate. An order
    /// whose exchange status is unknown may already be live on the exchange.
    /// Resubmitting it would double the intended exposure, so the order is
    /// frozen until a query proves what actually happened.
    /// </remarks>
    public bool RequiresReconciliation { get; private set; }

    public string? ReconciliationReason { get; private set; }

    /// <summary>
    /// True only when the order is provably not live on the exchange and may
    /// safely be submitted again. Unknown is never safe.
    /// </summary>
    public bool CanResubmit =>
        !RequiresReconciliation &&
        State is OrderState.Draft or OrderState.PendingValidation;

    public bool IsTerminal =>
        State is OrderState.Filled or OrderState.Canceled
            or OrderState.Rejected or OrderState.Expired;

    /// <summary>
    /// Records that the exchange acknowledged the order.
    /// </summary>
    public void MarkSubmitted(string? exchangeOrderId, DateTimeOffset occurredAtUtc)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException("A terminal order cannot be submitted again.");
        }

        if (!string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            ExchangeOrderId = exchangeOrderId.Trim();
        }

        State = OrderState.New;
        Transition(occurredAtUtc);
    }

    /// <summary>
    /// Records a partial fill. Fills are cumulative and may never exceed the
    /// ordered quantity or move backwards, which would silently lose exposure.
    /// </summary>
    public void MarkPartiallyFilled(decimal cumulativeFilledQuantity, DateTimeOffset occurredAtUtc)
    {
        if (cumulativeFilledQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeFilledQuantity), "A fill must be positive.");
        }

        if (cumulativeFilledQuantity > Quantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeFilledQuantity), "A fill cannot exceed the ordered quantity.");
        }

        if (cumulativeFilledQuantity < FilledQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeFilledQuantity), "Cumulative fills cannot decrease.");
        }

        FilledQuantity = cumulativeFilledQuantity;
        State = cumulativeFilledQuantity == Quantity ? OrderState.Filled : OrderState.PartiallyFilled;
        Transition(occurredAtUtc);
    }

    /// <summary>
    /// Freezes the order because its exchange outcome is unknown.
    /// </summary>
    public void MarkUnknown(string reason, DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        RequiresReconciliation = true;
        ReconciliationReason = reason.Trim();
        Transition(occurredAtUtc);
    }

    /// <summary>
    /// Clears the reconciliation freeze after the true exchange state has
    /// been established, and applies that state.
    /// </summary>
    /// <param name="resolvedState">
    /// The state proven by the exchange. It may not be
    /// <see cref="OrderState.Draft"/>: resolution must assert what happened.
    /// </param>
    public void ResolveReconciliation(
        OrderState resolvedState,
        decimal cumulativeFilledQuantity,
        string? exchangeOrderId,
        string reason,
        DateTimeOffset occurredAtUtc)
    {
        if (!RequiresReconciliation)
        {
            throw new InvalidOperationException("This order is not awaiting reconciliation.");
        }

        if (resolvedState == OrderState.Draft)
        {
            throw new ArgumentException(
                "Reconciliation must assert what happened on the exchange.", nameof(resolvedState));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A resolution reason is required.", nameof(reason));
        }

        if (cumulativeFilledQuantity < 0m || cumulativeFilledQuantity > Quantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeFilledQuantity), "Resolved fills must lie between zero and the ordered quantity.");
        }

        if (cumulativeFilledQuantity < FilledQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeFilledQuantity), "Resolved fills cannot be lower than fills already recorded.");
        }

        if (!string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            ExchangeOrderId = exchangeOrderId.Trim();
        }

        FilledQuantity = cumulativeFilledQuantity;
        State = resolvedState;
        RequiresReconciliation = false;
        ReconciliationReason = reason.Trim();
        Transition(occurredAtUtc);
    }

    public void MarkAccepted()
    {
        if (State == OrderState.Rejected || State == OrderState.Canceled || State == OrderState.Filled)
        {
            throw new InvalidOperationException("Order cannot transition from a terminal state to accepted.");
        }

        State = OrderState.Accepted;
        Version++;
    }

    public void MarkRejected(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        }

        State = OrderState.Rejected;
        Version++;
    }

    public void MarkFilled()
    {
        if (State == OrderState.Rejected || State == OrderState.Canceled)
        {
            throw new InvalidOperationException("Filled state cannot be assigned from a rejected or canceled order.");
        }

        State = OrderState.Filled;
        Version++;
    }

    public void MarkCanceled()
    {
        if (State == OrderState.Filled)
        {
            throw new InvalidOperationException("Filled orders cannot be canceled.");
        }

        State = OrderState.Canceled;
        Version++;
    }

    private void Transition(DateTimeOffset occurredAtUtc)
    {
        LastTransitionAtUtc = occurredAtUtc;
        Version++;
    }
}
