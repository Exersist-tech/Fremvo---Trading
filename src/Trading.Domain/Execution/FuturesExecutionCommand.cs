namespace Trading.Domain.Execution;

/// <summary>
/// A futures-only instruction for the isolated demo execution path.
/// </summary>
/// <remarks>
/// This is deliberately not an <see cref="ExecutionCommand"/>. Futures have
/// their own account identity and position semantics, and must never be
/// routed through the spot command path.
/// </remarks>
public sealed class FuturesExecutionCommand
{
    public FuturesExecutionCommand(
        Guid id,
        Guid futuresDemoAccountId,
        string symbol,
        TradeDirection direction,
        FuturesPositionDirection positionDirection,
        decimal quantity,
        decimal limitPrice,
        DateTimeOffset createdAtUtc,
        string clientOrderId,
        bool reduceOnly = true)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Futures execution command id is required.", nameof(id));
        }

        if (futuresDemoAccountId == Guid.Empty)
        {
            throw new ArgumentException("A futures demo account id is required.", nameof(futuresDemoAccountId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (limitPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(limitPrice), "Limit price must be positive.");
        }

        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            throw new ArgumentException("Client order id is required.", nameof(clientOrderId));
        }

        Id = id;
        FuturesDemoAccountId = futuresDemoAccountId;
        Symbol = symbol.Trim();
        Direction = direction;
        PositionDirection = positionDirection;
        Quantity = quantity;
        LimitPrice = limitPrice;
        CreatedAtUtc = createdAtUtc;
        ClientOrderId = clientOrderId.Trim();
        ReduceOnly = reduceOnly;
    }

    public Guid Id { get; }
    public Guid FuturesDemoAccountId { get; }
    public string Symbol { get; }
    public TradeDirection Direction { get; }
    public FuturesPositionDirection PositionDirection { get; }
    public decimal Quantity { get; }
    public decimal LimitPrice { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string ClientOrderId { get; }

    /// <summary>
    /// Explicitly carried to the venue without side-based inference. It
    /// defaults to the safer position-reducing behavior.
    /// </summary>
    public bool ReduceOnly { get; }
}

public enum FuturesPositionDirection
{
    LongPosition = 0,
    ShortPosition = 1
}

public interface IFuturesExecutionAdapter
{
    Task<ExecutionResult> ExecuteAsync(
        FuturesExecutionCommand command,
        CancellationToken cancellationToken = default);
}
