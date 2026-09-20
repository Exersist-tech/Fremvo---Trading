namespace Trading.Risk;

#pragma warning disable CA1720 // Long and short are financial directions, not numeric type names.
public enum PaperPositionDirection
{
    Long = 0,
    Short = 1
}
#pragma warning restore CA1720

public sealed record PaperExchangeFilters(decimal PriceTick, decimal QuantityStep, decimal MinimumQuantity, decimal MinimumNotional);

/// <summary>Per-worker limits supplied by the platform, not by a strategy.</summary>
public sealed record PaperWorkerSizingBudget(decimal RiskFraction, decimal MaxPerTradeNotional, decimal MaxTotalExposure, decimal MaxPositionQuantity);

/// <summary>Platform-owned approved profile and non-bypassable ceilings for paper training.</summary>
public sealed record PaperRiskSizingPolicy(
    decimal ApprovedStrategyRiskFraction,
    decimal PlatformMaxOpeningRiskFraction,
    decimal PlatformMaxPerTradeNotional,
    decimal PlatformMaxTotalExposure,
    decimal PlatformMaxPositionQuantity,
    TimeSpan MaximumInputAge,
    decimal AdditionRiskFractionMultiplier = 1m)
{
    public const decimal AbsoluteMaximumOpeningRiskFraction = 0.0025m;
}

/// <summary>Complete immutable input snapshot for pure, deterministic paper position sizing.</summary>
public sealed record PaperRiskSizingInput(
    decimal? WorkerEquity,
    decimal? AvailableCash,
    decimal? EntryPrice,
    decimal? ProtectiveStopPrice,
    PaperPositionDirection? Direction,
    decimal? CurrentPositionQuantity,
    decimal? CurrentExposure,
    PaperPositionDirection? CurrentPositionDirection,
    bool FavorableAddApproved,
    PaperExchangeFilters? ExchangeFilters,
    PaperWorkerSizingBudget? WorkerBudget,
    PaperRiskSizingPolicy? PlatformPolicy,
    DateTimeOffset? MarketSnapshotAtUtc,
    DateTimeOffset? AccountSnapshotAtUtc,
    DateTimeOffset? EvaluatedAtUtc);

public sealed record PaperRiskSizingLimits(
    decimal StrategyRiskFraction,
    decimal WorkerRiskFraction,
    decimal PlatformRiskFraction,
    decimal EffectiveRiskFraction,
    decimal EffectiveMaxPerTradeNotional,
    decimal EffectiveMaxTotalExposure,
    decimal EffectiveMaxPositionQuantity);

/// <summary>A denied result always has zero quantity, notional, and risk.</summary>
public sealed record PaperRiskSizingResult(
    bool IsAccepted,
    decimal Quantity,
    decimal Notional,
    decimal Risk,
    decimal RiskPerUnit,
    PaperRiskSizingLimits? EffectiveLimits,
    string Explanation)
{
    public static PaperRiskSizingResult Denied(string explanation, PaperRiskSizingLimits? limits = null) =>
        new(false, 0m, 0m, 0m, 0m, limits, explanation);
}

/// <summary>
/// Pure platform policy for calculating an opening or approved favorable-add paper quantity.
/// It does not submit orders, read accounts, or call an exchange.
/// </summary>
public sealed class PaperRiskPositionSizer
{
    public static PaperRiskSizingResult Size(PaperRiskSizingInput? input)
    {
        if (input is null)
            return PaperRiskSizingResult.Denied("A complete paper sizing input is required.");

        if (input.WorkerEquity is not > 0m || input.AvailableCash is not > 0m
            || input.EntryPrice is not > 0m || input.ProtectiveStopPrice is not > 0m
            || input.Direction is null || input.CurrentPositionQuantity is not >= 0m
            || input.CurrentExposure is not >= 0m || input.ExchangeFilters is null
            || input.WorkerBudget is null || input.PlatformPolicy is null)
            return PaperRiskSizingResult.Denied("Paper sizing input is missing or invalid.");

        var filters = input.ExchangeFilters;
        var budget = input.WorkerBudget;
        var policy = input.PlatformPolicy;
        var equity = input.WorkerEquity.Value;
        var cash = input.AvailableCash.Value;
        var currentPositionQuantity = input.CurrentPositionQuantity.Value;
        var currentExposure = input.CurrentExposure.Value;
        if (!AreValid(filters, budget, policy))
            return PaperRiskSizingResult.Denied("Paper sizing limits or exchange filters are invalid.");

        if (!AreFresh(input, policy))
            return PaperRiskSizingResult.Denied("Market or account sizing input is missing, stale, non-UTC, or future-dated.");

        if (!IsAligned(input.EntryPrice.Value, filters.PriceTick)
            || !IsAligned(input.ProtectiveStopPrice.Value, filters.PriceTick))
            return PaperRiskSizingResult.Denied("Entry and protective stop prices must align to the exchange price tick.");

        var entry = input.EntryPrice.Value;
        var stop = input.ProtectiveStopPrice.Value;
        var direction = input.Direction.Value;
        if ((direction == PaperPositionDirection.Long && stop >= entry)
            || (direction == PaperPositionDirection.Short && stop <= entry))
            return PaperRiskSizingResult.Denied("Protective stop is not protective for the requested direction.");

        var isAddition = currentPositionQuantity > 0m;
        if (isAddition && (!input.FavorableAddApproved || input.CurrentPositionDirection != direction))
            return PaperRiskSizingResult.Denied(
                "Position additions require existing platform-approved favorable-add controls and the same direction.");

        var platformRisk = Math.Min(policy.PlatformMaxOpeningRiskFraction, PaperRiskSizingPolicy.AbsoluteMaximumOpeningRiskFraction);
        var effectiveRiskFraction = Math.Min(Math.Min(policy.ApprovedStrategyRiskFraction, budget.RiskFraction), platformRisk);
        if (isAddition)
            effectiveRiskFraction *= policy.AdditionRiskFractionMultiplier;

        var limits = new PaperRiskSizingLimits(
            policy.ApprovedStrategyRiskFraction, budget.RiskFraction, platformRisk, effectiveRiskFraction,
            Math.Min(policy.PlatformMaxPerTradeNotional, budget.MaxPerTradeNotional),
            Math.Min(policy.PlatformMaxTotalExposure, budget.MaxTotalExposure),
            Math.Min(policy.PlatformMaxPositionQuantity, budget.MaxPositionQuantity));

        var riskPerUnit = Math.Abs(entry - stop);
        try
        {
            var riskQuantity = (effectiveRiskFraction * equity) / riskPerUnit;
            var cashQuantity = cash / entry;
            var notionalQuantity = limits.EffectiveMaxPerTradeNotional / entry;
            var remainingExposure = limits.EffectiveMaxTotalExposure - currentExposure;
            var remainingPosition = limits.EffectiveMaxPositionQuantity - currentPositionQuantity;
            if (remainingExposure <= 0m || remainingPosition <= 0m)
                return PaperRiskSizingResult.Denied("Current position or exposure already reaches a platform or worker limit.", limits);

            var quantity = FloorToStep(
                Math.Min(Math.Min(riskQuantity, cashQuantity),
                    Math.Min(notionalQuantity, Math.Min(remainingExposure / entry, remainingPosition))),
                filters.QuantityStep);

            if (quantity <= 0m)
                return PaperRiskSizingResult.Denied("No positive quantity remains after conservative exchange-step rounding.", limits);

            var notional = checked(quantity * entry);
            var risk = checked(quantity * riskPerUnit);
            var permittedRisk = checked(effectiveRiskFraction * equity);
            if (quantity < filters.MinimumQuantity || notional < filters.MinimumNotional)
                return PaperRiskSizingResult.Denied("Conservative quantity cannot satisfy the exchange minimum quantity or notional.", limits);
            if (notional > cash || notional > limits.EffectiveMaxPerTradeNotional
                || checked(currentExposure + notional) > limits.EffectiveMaxTotalExposure
                || checked(currentPositionQuantity + quantity) > limits.EffectiveMaxPositionQuantity
                || risk > permittedRisk)
                return PaperRiskSizingResult.Denied("Post-rounding quantity exceeds a required paper sizing limit.", limits);

            return new PaperRiskSizingResult(true, quantity, notional, risk, riskPerUnit, limits,
                "Paper quantity accepted after conservative risk, budget, exposure, and exchange-filter checks.");
        }
        catch (OverflowException)
        {
            return PaperRiskSizingResult.Denied("Paper sizing arithmetic overflowed; no exposure is allowed.", limits);
        }
    }

    private static bool AreValid(PaperExchangeFilters filters, PaperWorkerSizingBudget budget, PaperRiskSizingPolicy policy) =>
        filters.PriceTick > 0m && filters.QuantityStep > 0m && filters.MinimumQuantity > 0m && filters.MinimumNotional > 0m
        && budget.RiskFraction > 0m && budget.MaxPerTradeNotional > 0m && budget.MaxTotalExposure > 0m && budget.MaxPositionQuantity > 0m
        && policy.ApprovedStrategyRiskFraction > 0m && policy.PlatformMaxOpeningRiskFraction > 0m
        && policy.PlatformMaxOpeningRiskFraction <= PaperRiskSizingPolicy.AbsoluteMaximumOpeningRiskFraction
        && policy.PlatformMaxPerTradeNotional > 0m && policy.PlatformMaxTotalExposure > 0m && policy.PlatformMaxPositionQuantity > 0m
        && policy.MaximumInputAge >= TimeSpan.Zero && policy.AdditionRiskFractionMultiplier > 0m && policy.AdditionRiskFractionMultiplier <= 1m;

    private static bool AreFresh(PaperRiskSizingInput input, PaperRiskSizingPolicy policy)
    {
        if (!input.MarketSnapshotAtUtc.HasValue || !input.AccountSnapshotAtUtc.HasValue || !input.EvaluatedAtUtc.HasValue
            || input.MarketSnapshotAtUtc.Value.Offset != TimeSpan.Zero || input.AccountSnapshotAtUtc.Value.Offset != TimeSpan.Zero
            || input.EvaluatedAtUtc.Value.Offset != TimeSpan.Zero)
            return false;

        return input.MarketSnapshotAtUtc.Value <= input.EvaluatedAtUtc.Value
            && input.AccountSnapshotAtUtc.Value <= input.EvaluatedAtUtc.Value
            && input.EvaluatedAtUtc.Value - input.MarketSnapshotAtUtc.Value <= policy.MaximumInputAge
            && input.EvaluatedAtUtc.Value - input.AccountSnapshotAtUtc.Value <= policy.MaximumInputAge;
    }

    private static bool IsAligned(decimal value, decimal step) => value % step == 0m;

    private static decimal FloorToStep(decimal value, decimal step) => Math.Floor(value / step) * step;
}
