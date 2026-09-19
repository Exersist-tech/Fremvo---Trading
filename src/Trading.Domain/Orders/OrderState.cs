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
        bool closeOnly = false)
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

    public OrderState State { get; private set; }

    public int Version { get; private set; }

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
}
