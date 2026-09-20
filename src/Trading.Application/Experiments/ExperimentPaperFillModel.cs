using Trading.Backtesting;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;

namespace Trading.Application.Experiments;

public enum ExperimentPaperFillStatus
{
    Filled = 0,
    PartiallyFilled,
    Rejected
}

/// <summary>
/// Explicit, closed-input request for the isolated experiment fill model. Liquidity is required:
/// the model never assumes that an order fills merely because it was submitted.
/// </summary>
public sealed record ExperimentPaperFillRequest(
    string Symbol,
    TradeDirection Direction,
    decimal RequestedQuantity,
    decimal ReferencePrice,
    bool IsMakerOrder,
    decimal? LiquidityFillRatio,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Immutable evidence for every simulated execution attempt, including deterministic adjustments
/// and refusals. Values are decimal-only and have no exchange or account dependency.
/// </summary>
public sealed record ExperimentPaperFillRecord(
    ExperimentPaperFillRequest Request,
    ExperimentPaperFillStatus Status,
    decimal NormalizedRequestedQuantity,
    decimal FilledQuantity,
    decimal ExecutionPrice,
    decimal Notional,
    decimal Fee,
    decimal Slippage,
    bool QuantityAdjusted,
    bool PriceAdjusted,
    string Detail);

/// <summary>
/// Exchange-neutral deterministic fill model for the unregistered experiment path.
/// Quantity is conservatively rounded down to the configured step. Buy prices round up and sell
/// prices round down to the tick after adverse slippage. Every such change is retained in the
/// fill record; a zero, under-minimum, unaffordable, or unsupported partial fill is rejected.
/// </summary>
public sealed class ExperimentPaperFillModel
{
    private readonly FeeModel _fees;
    private readonly SlippageModel _slippage;
    private readonly ExchangeFilter _filter;
    private readonly List<ExperimentPaperFillRecord> _ledger = [];

    public ExperimentPaperFillModel(FeeModel fees, SlippageModel slippage, ExchangeFilter filter)
    {
        _fees = fees ?? throw new ArgumentNullException(nameof(fees));
        _slippage = slippage ?? throw new ArgumentNullException(nameof(slippage));
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
    }

    public IReadOnlyCollection<ExperimentPaperFillRecord> Ledger => _ledger.AsReadOnly();

    public ExperimentPaperFillRecord Evaluate(ExperimentPaperFillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var requestedQuantity = RoundDown(request.RequestedQuantity, _filter.StepSize);
        var quantityAdjusted = requestedQuantity != request.RequestedQuantity;
        if (requestedQuantity <= 0m)
            return Record(Rejected(request, requestedQuantity, quantityAdjusted, false,
                "Requested quantity rounds down to zero at the configured quantity step."));

        if (request.LiquidityFillRatio is not decimal fillRatio)
            return Record(Rejected(request, requestedQuantity, quantityAdjusted, false,
                "An explicit liquidity fill ratio is required; the model does not invent a full fill."));
        if (fillRatio <= 0m || fillRatio > 1m)
            return Record(Rejected(request, requestedQuantity, quantityAdjusted, false,
                "Liquidity fill ratio must be greater than zero and no greater than one."));

        var slippedPrice = _slippage.GetExecutionPrice(request.ReferencePrice, request.Direction == TradeDirection.Buy);
        var executionPrice = request.Direction == TradeDirection.Buy
            ? RoundUp(slippedPrice, _filter.TickSize)
            : RoundDown(slippedPrice, _filter.TickSize);
        var priceAdjusted = executionPrice != slippedPrice;
        if (executionPrice <= 0m)
            return Record(Rejected(request, requestedQuantity, quantityAdjusted, priceAdjusted,
                "Adverse slippage and tick rounding produce a non-positive execution price."));

        var filledQuantity = RoundDown(requestedQuantity * fillRatio, _filter.StepSize);
        if (filledQuantity <= 0m)
            return Record(Rejected(request, requestedQuantity, quantityAdjusted, priceAdjusted,
                "Explicit liquidity rounds down to zero at the configured quantity step.", executionPrice));

        var rejection = _filter.GetRejectionReason(filledQuantity, executionPrice);
        if (rejection is not null)
            return Record(Rejected(request, requestedQuantity, quantityAdjusted, priceAdjusted, rejection, executionPrice, filledQuantity));

        var notional = checked(filledQuantity * executionPrice);
        var fee = _fees.ComputeFee(notional, request.IsMakerOrder);
        var slippage = checked(Math.Abs(executionPrice - request.ReferencePrice) * filledQuantity);
        var status = filledQuantity == requestedQuantity ? ExperimentPaperFillStatus.Filled : ExperimentPaperFillStatus.PartiallyFilled;
        var detail = $"{(quantityAdjusted ? "Quantity conservatively rounded down. " : string.Empty)}" +
            $"{(priceAdjusted ? "Price adversarially rounded to tick. " : string.Empty)}" +
            $"Liquidity ratio {fillRatio} produced {status}.";
        return Record(new(request, status, requestedQuantity, filledQuantity, executionPrice, notional, fee, slippage,
            quantityAdjusted, priceAdjusted, detail));
    }

    /// <summary>
    /// Applies an evaluated fill only to the supplied isolated worker. The worker remains the
    /// authoritative cash, quantity, weighted-cost, and realized-P&amp;L ledger.
    /// </summary>
    public ExperimentPaperFillRecord EvaluateAndApply(ExperimentWorker worker, ExperimentPaperFillRequest request)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(worker.MarketSymbol, request.Symbol, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A paper fill may only apply to the worker's configured symbol.");

        var record = Evaluate(request);
        if (record.Status == ExperimentPaperFillStatus.Rejected)
            return record;

        var requiredCash = request.Direction == TradeDirection.Buy ? record.Notional + record.Fee : 0m;
        if (requiredCash > worker.CashBalance)
            return ReplaceLast(Rejected(record, "Worker cash is insufficient for the fee-inclusive paper fill."));
        if (request.Direction == TradeDirection.Sell && record.FilledQuantity > worker.PositionQuantity)
            return ReplaceLast(Rejected(record, "Worker base quantity is insufficient for the paper fill."));

        try
        {
            worker.ApplyPaperTrade(
                record.FilledQuantity,
                record.ExecutionPrice,
                record.Fee,
                request.Direction == TradeDirection.Buy ? "buy" : "sell",
                request.OccurredAtUtc);
            return record;
        }
        catch (InvalidOperationException exception)
        {
            return ReplaceLast(Rejected(record, exception.Message));
        }
    }

    private ExperimentPaperFillRecord Record(ExperimentPaperFillRecord record)
    {
        _ledger.Add(record);
        return record;
    }

    private ExperimentPaperFillRecord ReplaceLast(ExperimentPaperFillRecord record)
    {
        _ledger[^1] = record;
        return record;
    }

    private static ExperimentPaperFillRecord Rejected(
        ExperimentPaperFillRequest request,
        decimal normalizedRequestedQuantity,
        bool quantityAdjusted,
        bool priceAdjusted,
        string detail,
        decimal executionPrice = 0m,
        decimal filledQuantity = 0m) =>
        new(request, ExperimentPaperFillStatus.Rejected, normalizedRequestedQuantity, filledQuantity, executionPrice,
            0m, 0m, 0m, quantityAdjusted, priceAdjusted, detail);

    private static ExperimentPaperFillRecord Rejected(ExperimentPaperFillRecord record, string detail) =>
        record with { Status = ExperimentPaperFillStatus.Rejected, FilledQuantity = 0m, Notional = 0m, Fee = 0m, Slippage = 0m, Detail = detail };

    private static void ValidateRequest(ExperimentPaperFillRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            throw new ArgumentException("Symbol is required.", nameof(request));
        if (request.RequestedQuantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(request), "Requested quantity must be positive.");
        if (request.ReferencePrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(request), "Reference price must be positive.");
        if (request.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Occurrence time must be UTC.", nameof(request));
    }

    private static decimal RoundDown(decimal value, decimal increment) => value - (value % increment);

    private static decimal RoundUp(decimal value, decimal increment)
    {
        var roundedDown = RoundDown(value, increment);
        return roundedDown == value ? value : roundedDown + increment;
    }
}
