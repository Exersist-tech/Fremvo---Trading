using Trading.Domain.Execution;

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
        decimal unrealizedPnl = 0m,
        TradingMode mode = TradingMode.Paper)
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
        Mode = mode;
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

    /// <summary>
    /// Which book this position belongs to. Simulated and real positions are
    /// never mixed in one list: a paper position shown beside a real one, or
    /// summed with it, would misstate actual exposure. The default is
    /// <see cref="TradingMode.Paper"/> so a caller that omits it produces a
    /// simulated record rather than one that claims to be real.
    /// </summary>
    public TradingMode Mode { get; }

    public decimal UnrealizedPnl { get; private set; }

    public PositionStatus Status { get; private set; }

    /// <summary>
    /// Price at which the position should be closed to limit loss, if one is set.
    /// </summary>
    public decimal? StopLossPrice { get; private set; }

    /// <summary>
    /// Price at which the position should be closed to take profit, if one is set.
    /// </summary>
    public decimal? TakeProfitPrice { get; private set; }

    public bool HasProtectiveExits => StopLossPrice.HasValue || TakeProfitPrice.HasValue;

    /// <summary>
    /// Sets the protective exit levels for the position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each level is validated against the direction of the position. A stop
    /// placed on the profitable side, or a target placed on the losing side,
    /// would trigger the moment it is evaluated and close the position at the
    /// opposite of the intended outcome, so it is refused rather than stored.
    /// </para>
    /// <para>
    /// Passing <see langword="null"/> for a level removes it. Removing a stop
    /// is permitted but is a deliberate act: it is never removed implicitly by
    /// another operation.
    /// </para>
    /// </remarks>
    public void SetProtectiveExits(decimal? stopLossPrice, decimal? takeProfitPrice, DateTimeOffset occurredAtUtc)
    {
        if (Status is PositionStatus.Flat or PositionStatus.Liquidated)
        {
            throw new InvalidOperationException("A closed position cannot be given exit levels.");
        }

        if (stopLossPrice is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(stopLossPrice), "Stop price must be positive.");
        }

        if (takeProfitPrice is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(takeProfitPrice), "Target price must be positive.");
        }

        if (Direction == PositionDirection.DirectionLong)
        {
            if (stopLossPrice >= EntryPrice)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopLossPrice),
                    "A long position's stop must be below its entry price, otherwise it closes the position at a loss the moment it is evaluated.");
            }

            if (takeProfitPrice <= EntryPrice)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(takeProfitPrice),
                    "A long position's target must be above its entry price.");
            }
        }
        else
        {
            if (stopLossPrice <= EntryPrice)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopLossPrice),
                    "A short position's stop must be above its entry price, otherwise it closes the position at a loss the moment it is evaluated.");
            }

            if (takeProfitPrice >= EntryPrice)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(takeProfitPrice),
                    "A short position's target must be below its entry price.");
            }
        }

        StopLossPrice = stopLossPrice;
        TakeProfitPrice = takeProfitPrice;
        Transition(occurredAtUtc);
    }

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
