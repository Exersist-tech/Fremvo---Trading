namespace Trading.Domain.Futures;

public enum FuturesPositionSide
{
    LongPosition = 0,
    ShortPosition = 1
}

public enum FuturesPositionStatus
{
    Open = 0,
    Closed,
    Liquidated
}

public enum FuturesMarginMode
{
    Isolated = 0,
    Cross
}

/// <summary>
/// Exchange-neutral, observed state for one linear futures position.
/// This aggregate is intentionally independent from Spot positions and order execution.
/// </summary>
public sealed class FuturesPosition
{
    public FuturesPosition(
        Guid id,
        string contract,
        FuturesPositionSide side,
        decimal quantity,
        decimal entryPrice,
        decimal markPrice,
        decimal margin,
        decimal liquidationPrice,
        decimal fundingAccrued,
        decimal realizedPnl,
        decimal observedLeverage,
        FuturesMarginMode marginMode,
        DateTimeOffset valuedAtUtc)
        : this(
            id,
            ValidateContract(contract),
            ValidateSide(side),
            ValidatePositive(quantity, nameof(quantity)),
            ValidatePositive(entryPrice, nameof(entryPrice)),
            ValidatePositive(markPrice, nameof(markPrice)),
            ValidateNonNegative(margin, nameof(margin)),
            ValidatePositive(liquidationPrice, nameof(liquidationPrice)),
            fundingAccrued,
            realizedPnl,
            ValidatePositive(observedLeverage, nameof(observedLeverage)),
            ValidateMarginMode(marginMode),
            ValidateInitialTimestamp(valuedAtUtc),
            FuturesPositionStatus.Open)
    {
    }

    private FuturesPosition(
        Guid id,
        string contract,
        FuturesPositionSide side,
        decimal quantity,
        decimal entryPrice,
        decimal markPrice,
        decimal margin,
        decimal liquidationPrice,
        decimal fundingAccrued,
        decimal realizedPnl,
        decimal observedLeverage,
        FuturesMarginMode marginMode,
        DateTimeOffset valuedAtUtc,
        FuturesPositionStatus status)
    {
        Id = id == Guid.Empty
            ? throw new ArgumentException("Position id is required.", nameof(id))
            : id;
        Contract = contract;
        Side = side;
        Quantity = quantity;
        EntryPrice = entryPrice;
        MarkPrice = markPrice;
        Margin = margin;
        LiquidationPrice = liquidationPrice;
        FundingAccrued = fundingAccrued;
        RealizedPnl = realizedPnl;
        ObservedLeverage = observedLeverage;
        MarginMode = marginMode;
        ValuedAtUtc = valuedAtUtc;
        Status = status;
    }

    public Guid Id { get; }

    public string Contract { get; }

    public FuturesPositionSide Side { get; }

    public decimal Quantity { get; }

    public decimal EntryPrice { get; }

    public decimal MarkPrice { get; }

    public decimal Margin { get; }

    public decimal LiquidationPrice { get; }

    public decimal FundingAccrued { get; }

    public decimal RealizedPnl { get; }

    /// <summary>
    /// The leverage reported when this aggregate was observed. It is context only:
    /// position tracking never changes it.
    /// </summary>
    public decimal ObservedLeverage { get; }

    public FuturesMarginMode MarginMode { get; }

    public DateTimeOffset ValuedAtUtc { get; }

    public FuturesPositionStatus Status { get; }

    public bool IsTerminal => Status is FuturesPositionStatus.Closed or FuturesPositionStatus.Liquidated;

    public decimal UnrealizedPnl => Quantity == 0m
        ? 0m
        : Side == FuturesPositionSide.LongPosition
            ? (MarkPrice - EntryPrice) * Quantity
            : (EntryPrice - MarkPrice) * Quantity;

    public FuturesPosition ObserveValuation(
        decimal markPrice,
        decimal margin,
        decimal liquidationPrice,
        DateTimeOffset valuedAtUtc)
    {
        EnsureOpen();
        ValidateNextTimestamp(valuedAtUtc);

        return With(
            markPrice: ValidatePositive(markPrice, nameof(markPrice)),
            margin: ValidateNonNegative(margin, nameof(margin)),
            liquidationPrice: ValidatePositive(liquidationPrice, nameof(liquidationPrice)),
            valuedAtUtc: valuedAtUtc);
    }

    /// <summary>
    /// Applies an exchange-observed funding debit or credit. Funding is never inferred
    /// from elapsed time, prices, margin, or leverage.
    /// </summary>
    public FuturesPosition AccrueObservedFunding(decimal amount, DateTimeOffset observedAtUtc)
    {
        EnsureOpen();
        ValidateNextTimestamp(observedAtUtc);

        return With(
            fundingAccrued: FundingAccrued + amount,
            valuedAtUtc: observedAtUtc);
    }

    /// <summary>
    /// Applies a reduce-only fill. The requested reduction must be no larger than the
    /// remaining position, so it can never increase exposure or reverse direction.
    /// </summary>
    public FuturesPosition ApplyReduceOnly(
        decimal quantityToReduce,
        decimal executionPrice,
        DateTimeOffset observedAtUtc)
    {
        EnsureOpen();
        ValidateNextTimestamp(observedAtUtc);
        ValidatePositive(quantityToReduce, nameof(quantityToReduce));
        ValidatePositive(executionPrice, nameof(executionPrice));

        if (quantityToReduce > Quantity)
        {
            throw new InvalidOperationException("A reduce-only fill cannot exceed the remaining quantity.");
        }

        var realizedDelta = Side == FuturesPositionSide.LongPosition
            ? (executionPrice - EntryPrice) * quantityToReduce
            : (EntryPrice - executionPrice) * quantityToReduce;
        var remainingQuantity = Quantity - quantityToReduce;

        return With(
            quantity: remainingQuantity,
            markPrice: executionPrice,
            realizedPnl: RealizedPnl + realizedDelta,
            valuedAtUtc: observedAtUtc,
            status: remainingQuantity == 0m ? FuturesPositionStatus.Closed : FuturesPositionStatus.Open);
    }

    public FuturesPosition MarkLiquidated(DateTimeOffset observedAtUtc)
    {
        EnsureOpen();
        ValidateNextTimestamp(observedAtUtc);

        return With(
            quantity: 0m,
            markPrice: LiquidationPrice,
            realizedPnl: RealizedPnl + UnrealizedPnl,
            valuedAtUtc: observedAtUtc,
            status: FuturesPositionStatus.Liquidated);
    }

    private FuturesPosition With(
        decimal? quantity = null,
        decimal? markPrice = null,
        decimal? margin = null,
        decimal? liquidationPrice = null,
        decimal? fundingAccrued = null,
        decimal? realizedPnl = null,
        DateTimeOffset? valuedAtUtc = null,
        FuturesPositionStatus? status = null) =>
        new(
            Id,
            Contract,
            Side,
            quantity ?? Quantity,
            EntryPrice,
            markPrice ?? MarkPrice,
            margin ?? Margin,
            liquidationPrice ?? LiquidationPrice,
            fundingAccrued ?? FundingAccrued,
            realizedPnl ?? RealizedPnl,
            ObservedLeverage,
            MarginMode,
            valuedAtUtc ?? ValuedAtUtc,
            status ?? Status);

    private void EnsureOpen()
    {
        if (Status != FuturesPositionStatus.Open)
        {
            throw new InvalidOperationException("A closed or liquidated futures position cannot be changed.");
        }
    }

    private void ValidateNextTimestamp(DateTimeOffset timestamp)
    {
        ValidateTimestamp(timestamp, nameof(timestamp));

        if (timestamp <= ValuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "Observed timestamps must advance.");
        }
    }

    private static string ValidateContract(string contract)
    {
        if (string.IsNullOrWhiteSpace(contract))
        {
            throw new ArgumentException("Contract is required.", nameof(contract));
        }

        return contract.Trim();
    }

    private static FuturesPositionSide ValidateSide(FuturesPositionSide side)
    {
        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side), "Position side is invalid.");
        }

        return side;
    }

    private static FuturesMarginMode ValidateMarginMode(FuturesMarginMode marginMode)
    {
        if (!Enum.IsDefined(marginMode))
        {
            throw new ArgumentOutOfRangeException(nameof(marginMode), "Margin mode is invalid.");
        }

        return marginMode;
    }

    private static decimal ValidatePositive(decimal value, string parameterName)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value must be positive.");
        }

        return value;
    }

    private static decimal ValidateNonNegative(decimal value, string parameterName)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Value cannot be negative.");
        }

        return value;
    }

    private static DateTimeOffset ValidateInitialTimestamp(DateTimeOffset timestamp)
    {
        ValidateTimestamp(timestamp, nameof(timestamp));
        return timestamp;
    }

    private static void ValidateTimestamp(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }

        if (timestamp > DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timestamp cannot be in the future.");
        }
    }
}
