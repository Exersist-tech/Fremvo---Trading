namespace Trading.Domain.Positions;

public enum PositionDirection
{
    DirectionLong = 0,
    DirectionShort = 1
}

public enum PositionStatus
{
    Flat = 0,
    Open,
    Closing,
    Liquidated,
    ReducedOnly
}

public sealed class Position
{
    public Position(
        Guid id,
        Guid userId,
        Guid strategyId,
        string symbol,
        PositionDirection direction,
        decimal quantity,
        decimal entryPrice,
        decimal markPrice,
        DateTimeOffset openedAtUtc,
        decimal unrealizedPnl = 0m)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Position id is required.", nameof(id));
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

        if (entryPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(entryPrice), "Entry price must be positive.");
        }

        if (markPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(markPrice), "Mark price must be positive.");
        }

        Id = id;
        UserId = userId;
        StrategyId = strategyId;
        Symbol = symbol.Trim();
        Direction = direction;
        Quantity = quantity;
        EntryPrice = entryPrice;
        MarkPrice = markPrice;
        OpenedAtUtc = openedAtUtc;
        UnrealizedPnl = unrealizedPnl;
        Status = PositionStatus.Open;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public Guid StrategyId { get; }

    public string Symbol { get; }

    public PositionDirection Direction { get; }

    public decimal Quantity { get; private set; }

    public decimal EntryPrice { get; }

    public decimal MarkPrice { get; private set; }

    public DateTimeOffset OpenedAtUtc { get; }

    public decimal UnrealizedPnl { get; private set; }

    public PositionStatus Status { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset? LastTransitionAtUtc { get; private set; }

    /// <summary>
    /// True when exposure may still be increased. Closed, liquidated and
    /// restricted positions never permit an increase.
    /// </summary>
    public bool PermitsIncrease => Status == PositionStatus.Open;

    /// <summary>
    /// Restricts the position so only exposure-reducing orders are accepted.
    /// </summary>
    /// <remarks>
    /// The restriction is deliberately one-way. Lifting it requires a new
    /// evaluation rather than a state transition, so a degraded feed or a
    /// risk breach cannot be undone by the same code path that noticed it.
    /// </remarks>
    public void RestrictToReduceOnly(DateTimeOffset occurredAtUtc)
    {
        if (Status is PositionStatus.Flat or PositionStatus.Liquidated)
        {
            throw new InvalidOperationException("A closed position cannot be restricted.");
        }

        Status = PositionStatus.ReducedOnly;
        Transition(occurredAtUtc);
    }

    /// <summary>
    /// Marks the position as closing. Only exposure-removing orders are
    /// accepted from this point.
    /// </summary>
    public void BeginClosing(DateTimeOffset occurredAtUtc)
    {
        if (Status is PositionStatus.Flat or PositionStatus.Liquidated)
        {
            throw new InvalidOperationException("A closed position cannot begin closing.");
        }

        Status = PositionStatus.Closing;
        Transition(occurredAtUtc);
    }

    public void UpdateMarkPrice(decimal newMarkPrice)
    {
        if (newMarkPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(newMarkPrice), "Mark price must be positive.");
        }

        MarkPrice = newMarkPrice;
        RecalculateUnrealisedPnl();
    }

    public void Reduce(decimal quantityToReduce)
    {
        if (quantityToReduce <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityToReduce), "Reduction quantity must be positive.");
        }

        if (quantityToReduce > Quantity)
        {
            throw new InvalidOperationException("Reduction cannot exceed the current quantity.");
        }

        Quantity -= quantityToReduce;

        if (Quantity == 0m)
        {
            Status = PositionStatus.Flat;
        }

        // Unrealised profit and loss is a function of the quantity still open,
        // so it must be recomputed here. Leaving it untouched would report the
        // exposure of a position size that is no longer held, overstating both
        // gains and losses after every partial reduction.
        RecalculateUnrealisedPnl();

        // The concurrency token has to move whenever persisted state changes,
        // otherwise a concurrent writer can overwrite this reduction.
        Version++;
    }

    /// <summary>
    /// Unrealised profit and loss on the quantity still open, in quote
    /// currency. A flat position has no exposure and therefore none.
    /// </summary>
    private void RecalculateUnrealisedPnl()
    {
        if (Quantity == 0m)
        {
            UnrealizedPnl = 0m;
            return;
        }

        UnrealizedPnl = Direction == PositionDirection.DirectionLong
            ? (MarkPrice - EntryPrice) * Quantity
            : (EntryPrice - MarkPrice) * Quantity;
    }

    public void Liquidate()
    {
        Status = PositionStatus.Liquidated;
        Quantity = 0m;
        Version++;
    }

    private void Transition(DateTimeOffset occurredAtUtc)
    {
        LastTransitionAtUtc = occurredAtUtc;
        Version++;
    }
}
