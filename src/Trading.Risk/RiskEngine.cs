using System.Collections.Generic;
using System.Linq;

namespace Trading.Risk;

public enum RiskLimitType
{
    None = 0,
    MaxPositionSize,
    MaxDailyLoss,
    MaxExposure,
    MaxConcurrentOrders,
    MaxOrderSize,
    MaxLeverage,
    MaxOpenPositions,
    MaxDrawdown,
    MaxOrderRate
}

public sealed class RiskLimit
{
    public RiskLimit(RiskLimitType type, decimal value, string? reason = null)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Risk limit value cannot be negative.");
        }

        Type = type;
        Value = value;
        Reason = reason;
    }

    public RiskLimitType Type { get; }

    public decimal Value { get; }

    public string? Reason { get; }
}

public sealed class RiskEvaluationResult
{
    public RiskEvaluationResult(
        bool isAllowed,
        string? reason = null,
        IReadOnlyCollection<RiskLimit>? activeLimits = null)
    {
        IsAllowed = isAllowed;
        Reason = reason;
        ActiveLimits = activeLimits ?? Array.Empty<RiskLimit>();
    }

    public bool IsAllowed { get; }

    public string? Reason { get; }

    public IReadOnlyCollection<RiskLimit> ActiveLimits { get; }
}

public enum HaltScope
{
    Global = 0,
    Market,
    Account,
    Strategy
}

public sealed class TradingModeFlags
{
    public bool EmergencyStop { get; set; }

    public bool MarketHalt { get; set; }

    public bool AccountHalted { get; set; }

    public bool StrategyHalted { get; set; }

    public bool CloseOnlyMode { get; set; }

    public bool ReduceOnlyMode { get; set; }

    public bool IsAnyHalt => EmergencyStop || MarketHalt || AccountHalted || StrategyHalted;
}

public sealed class HaltSwitch
{
    public HaltSwitch(HaltScope scope, bool isEnabled, string? reason = null)
    {
        if (scope == HaltScope.Global && !isEnabled)
        {
            throw new ArgumentException("A global halt switch cannot be disabled explicitly in this model.", nameof(isEnabled));
        }

        Scope = scope;
        IsEnabled = isEnabled;
        Reason = reason ?? "Trading is halted by policy.";
    }

    public HaltScope Scope { get; }

    public bool IsEnabled { get; }

    public string Reason { get; }
}

public sealed class StalenessPolicy
{
    public StalenessPolicy(TimeSpan maxAge, bool requiresFreshData = true)
    {
        if (maxAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Maximum staleness age cannot be negative.");
        }

        MaxAge = maxAge;
        RequiresFreshData = requiresFreshData;
    }

    public TimeSpan MaxAge { get; }

    public bool RequiresFreshData { get; }

    public bool IsStale(DateTimeOffset? lastUpdatedUtc, DateTimeOffset nowUtc)
    {
        if (!RequiresFreshData || !lastUpdatedUtc.HasValue)
        {
            return RequiresFreshData && !lastUpdatedUtc.HasValue;
        }

        return nowUtc - lastUpdatedUtc.Value > MaxAge;
    }
}

public sealed class RiskEngine
{
    private readonly IReadOnlyCollection<RiskLimit> _mandatoryLimits;

    public RiskEngine(IEnumerable<RiskLimit>? mandatoryLimits = null)
    {
        _mandatoryLimits = mandatoryLimits?.ToArray() ?? Array.Empty<RiskLimit>();
    }

    public RiskEvaluationResult Evaluate(
        decimal proposedExposure,
        decimal currentExposure,
        decimal dailyPnL,
        int openOrders,
        int openPositions,
        decimal maxPositionSize,
        decimal maxNotional,
        bool dataIsStale,
        bool accountIsHalted,
        bool strategyIsHalted,
        bool closeOnlyMode,
        bool reduceOnlyMode,
        bool duplicateOrderDetected,
        bool orderIdempotencyConflict,
        bool marketHalt,
        bool emergencyStop,
        TradingModeFlags? tradingMode = null,
        HaltSwitch? haltSwitch = null,
        StalenessPolicy? stalenessPolicy = null,
        RiskLimitHierarchy? riskLimitHierarchy = null,
        DateTimeOffset? lastDataUpdateUtc = null,
        DateTimeOffset? nowUtc = null,
        ProvingRestriction? provingRestriction = null,
        decimal? proposedQuantity = null)
    {
        var active = new List<RiskLimit>();

        if (proposedExposure < 0m || currentExposure < 0m || maxPositionSize <= 0m || maxNotional <= 0m
            || openOrders < 0 || openPositions < 0 || proposedQuantity is <= 0m)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxExposure, 0m, "Risk evaluation input is invalid."));
            return new RiskEvaluationResult(false, "Risk evaluation input is invalid.", active);
        }

        if (riskLimitHierarchy is not null && !proposedQuantity.HasValue)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxPositionSize, 0m, "Order quantity is required for hierarchy evaluation."));
            return new RiskEvaluationResult(false, "Order quantity is required for hierarchy evaluation.", active);
        }

        var effectiveMaxPositionSize = riskLimitHierarchy is null
            ? maxPositionSize
            : Math.Min(maxPositionSize, riskLimitHierarchy.EffectiveMaxPositionSize);
        var effectiveMaxExposure = riskLimitHierarchy is null
            ? maxNotional
            : Math.Min(maxNotional, riskLimitHierarchy.EffectiveMaxExposure);

        var effectiveEmergencyStop = emergencyStop || (tradingMode?.EmergencyStop ?? false);
        var effectiveMarketHalt = marketHalt || (tradingMode?.MarketHalt ?? false) || (haltSwitch?.Scope == HaltScope.Market && haltSwitch.IsEnabled);
        var effectiveAccountHalted = accountIsHalted || (tradingMode?.AccountHalted ?? false) || (haltSwitch?.Scope == HaltScope.Account && haltSwitch.IsEnabled);
        var effectiveStrategyHalted = strategyIsHalted || (tradingMode?.StrategyHalted ?? false) || (haltSwitch?.Scope == HaltScope.Strategy && haltSwitch.IsEnabled);
        var effectiveCloseOnlyMode = closeOnlyMode || (tradingMode?.CloseOnlyMode ?? false);
        var effectiveReduceOnlyMode = reduceOnlyMode || (tradingMode?.ReduceOnlyMode ?? false);

        if (effectiveEmergencyStop || effectiveMarketHalt || effectiveAccountHalted || effectiveStrategyHalted)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxExposure, 0m, "Trading is halted by policy."));
            return new RiskEvaluationResult(false, "Trading is currently halted.", active);
        }

        // Evaluate the policy against the actual data age. A missing timestamp is treated as
        // stale by StalenessPolicy, so the fail-safe is to block rather than to trade blind.
        var effectiveDataIsStale = dataIsStale
            || (stalenessPolicy is not null
                && stalenessPolicy.IsStale(lastDataUpdateUtc, nowUtc ?? DateTimeOffset.UtcNow));

        if (effectiveDataIsStale)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxExposure, 0m, "Relevant market or account data is stale."));
            return new RiskEvaluationResult(false, "Market or account data is stale.", active);
        }

        if (duplicateOrderDetected || orderIdempotencyConflict)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxConcurrentOrders, 0m, "Duplicate or non-idempotent order request detected."));
            return new RiskEvaluationResult(false, "Duplicate or conflicting order state detected.", active);
        }

        if (effectiveCloseOnlyMode && proposedExposure > 0m)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxExposure, 0m, "Close-only mode prevents new exposure."));
            return new RiskEvaluationResult(false, "Close-only mode is active.", active);
        }

        if (effectiveReduceOnlyMode && proposedExposure > 0m)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxExposure, 0m, "Reduce-only mode prevents increasing exposure."));
            return new RiskEvaluationResult(false, "Reduce-only mode is active.", active);
        }

        var proposedTotal = currentExposure + proposedExposure;

        var quantityToCheck = proposedQuantity ?? proposedExposure;
        if (quantityToCheck > effectiveMaxPositionSize)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxPositionSize, effectiveMaxPositionSize, "Proposed quantity exceeds the configured maximum position size."));
            return new RiskEvaluationResult(false, "Proposed quantity exceeds maximum position size.", active);
        }

        if (proposedTotal > effectiveMaxExposure)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxExposure, effectiveMaxExposure, "Combined exposure exceeds the maximum notional."));
            return new RiskEvaluationResult(false, "Exposure exceeds maximum notional.", active);
        }

        if (openOrders > 0 && openOrders >= 100)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxConcurrentOrders, 100m, "Too many concurrent orders are already pending."));
            return new RiskEvaluationResult(false, "Concurrent order limit reached.", active);
        }

        if (openPositions > 0 && openPositions >= 50)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxOpenPositions, 50m, "Too many open positions are active."));
            return new RiskEvaluationResult(false, "Open position limit reached.", active);
        }

        if (dailyPnL < -500m)
        {
            active.Add(new RiskLimit(RiskLimitType.MaxDailyLoss, 500m, "Daily loss limit reached."));
            return new RiskEvaluationResult(false, "Daily loss limit reached.", active);
        }

        // The proving bounds are a platform restriction, not a user preference,
        // so they are checked alongside the mandatory ceilings and cannot be
        // widened by anything the user configures.
        if (provingRestriction?.Violation() is { } provingViolation)
        {
            active.Add(new RiskLimit(
                RiskLimitType.MaxOrderSize,
                provingRestriction.NotionalCeiling ?? 0m,
                provingViolation));
            return new RiskEvaluationResult(false, provingViolation, active);
        }

        foreach (var limit in _mandatoryLimits)
        {
            if (limit.Type == RiskLimitType.MaxExposure && proposedTotal > limit.Value)
            {
                active.Add(limit);
                return new RiskEvaluationResult(false, "Mandatory platform ceiling exceeded.", active);
            }
        }

        return new RiskEvaluationResult(true, "Order is within configured risk limits.", active);
    }
}
