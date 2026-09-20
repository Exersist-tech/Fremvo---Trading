using Trading.Domain.Market;

namespace Trading.Domain.Universe;

/// <summary>
/// The twelve eligibility gates. A configured pair may be used for a purpose
/// only when every gate passes.
/// </summary>
public enum EligibilityGate
{
    None = 0,
    ExchangeStatus = 1,
    Permissions = 2,
    QuoteAsset = 3,
    FiltersLoaded = 4,
    DataHealth = 5,
    HistoryComplete = 6,
    Liquidity = 7,
    Spread = 8,
    Slippage = 9,
    ListingAge = 10,
    AssetClass = 11,
    StrategyApproval = 12
}

/// <summary>
/// The result of one gate, recorded so any eligibility decision can be
/// explained after the fact.
/// </summary>
public sealed record EligibilityGateResult(
    EligibilityGate Gate,
    bool Passed,
    string Detail,
    decimal? MeasuredValue = null,
    decimal? Threshold = null);

/// <summary>
/// Thresholds for one timeframe band. Configuration may only make a
/// requirement stricter than the platform floor, never more permissive.
/// </summary>
public sealed class EligibilityThresholds
{
    public EligibilityThresholds(
        decimal minimumRollingQuoteVolume,
        decimal minimumMedianQuoteVolume,
        decimal maximumSpread,
        decimal maximumEstimatedSlippage,
        int minimumHistoryCandles,
        TimeSpan minimumListingAge,
        TimeSpan maximumEvidenceAge)
    {
        if (minimumRollingQuoteVolume < 0m || minimumMedianQuoteVolume < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumRollingQuoteVolume), "Volume thresholds cannot be negative.");
        }

        if (maximumSpread <= 0m || maximumEstimatedSlippage <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSpread), "Spread and slippage ceilings must be positive.");
        }

        if (minimumHistoryCandles <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumHistoryCandles), "A positive minimum history is required.");
        }

        if (maximumEvidenceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEvidenceAge), "A positive maximum evidence age is required.");
        }

        MinimumRollingQuoteVolume = minimumRollingQuoteVolume;
        MinimumMedianQuoteVolume = minimumMedianQuoteVolume;
        MaximumSpread = maximumSpread;
        MaximumEstimatedSlippage = maximumEstimatedSlippage;
        MinimumHistoryCandles = minimumHistoryCandles;
        MinimumListingAge = minimumListingAge;
        MaximumEvidenceAge = maximumEvidenceAge;
    }

    public decimal MinimumRollingQuoteVolume { get; }

    public decimal MinimumMedianQuoteVolume { get; }

    public decimal MaximumSpread { get; }

    public decimal MaximumEstimatedSlippage { get; }

    public int MinimumHistoryCandles { get; }

    public TimeSpan MinimumListingAge { get; }

    public TimeSpan MaximumEvidenceAge { get; }

    /// <summary>
    /// Combines this configuration with the mandatory platform floor, taking
    /// the stricter of the two for every field. An operator therefore cannot
    /// configure a threshold more permissive than the floor.
    /// </summary>
    public EligibilityThresholds ConstrainedBy(EligibilityThresholds floor)
    {
        ArgumentNullException.ThrowIfNull(floor);

        return new EligibilityThresholds(
            Math.Max(MinimumRollingQuoteVolume, floor.MinimumRollingQuoteVolume),
            Math.Max(MinimumMedianQuoteVolume, floor.MinimumMedianQuoteVolume),
            Math.Min(MaximumSpread, floor.MaximumSpread),
            Math.Min(MaximumEstimatedSlippage, floor.MaximumEstimatedSlippage),
            Math.Max(MinimumHistoryCandles, floor.MinimumHistoryCandles),
            MinimumListingAge > floor.MinimumListingAge ? MinimumListingAge : floor.MinimumListingAge,
            MaximumEvidenceAge < floor.MaximumEvidenceAge ? MaximumEvidenceAge : floor.MaximumEvidenceAge);
    }
}

/// <summary>
/// The explainable outcome of evaluating every gate for one scope.
/// </summary>
public sealed class EligibilityEvaluation
{
    public EligibilityEvaluation(
        Guid instrumentId,
        EligibilityScope scope,
        DateTimeOffset evaluatedAtUtc,
        IReadOnlyList<EligibilityGateResult> gateResults)
    {
        ArgumentNullException.ThrowIfNull(gateResults);

        if (gateResults.Count == 0)
        {
            throw new ArgumentException("An evaluation must record at least one gate.", nameof(gateResults));
        }

        InstrumentId = instrumentId;
        Scope = scope;
        EvaluatedAtUtc = evaluatedAtUtc;
        GateResults = gateResults;
    }

    public Guid InstrumentId { get; }

    public EligibilityScope Scope { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }

    public IReadOnlyList<EligibilityGateResult> GateResults { get; }

    public bool Passed => GateResults.All(result => result.Passed);

    public IReadOnlyList<EligibilityGateResult> FailedGates =>
        GateResults.Where(result => !result.Passed).ToList();

    /// <summary>
    /// A human-readable explanation of why the instrument is not eligible,
    /// or a statement that every gate passed.
    /// </summary>
    public string Explain() =>
        Passed
            ? $"{Scope}: all {GateResults.Count} gates passed."
            : $"{Scope}: blocked by {string.Join(", ", FailedGates.Select(gate => $"{gate.Gate} ({gate.Detail})"))}.";
}

/// <summary>
/// Inputs describing the state of the instrument's data and strategy
/// approval at the moment of evaluation.
/// </summary>
public sealed record EligibilityInputs(
    int AvailableHistoryCandles,
    bool StrategyApproved,
    string StrategyApprovalDetail);

/// <summary>
/// Evaluates every eligibility gate for one scope.
/// </summary>
/// <remarks>
/// Deterministic and free of I/O: identical inputs produce an identical
/// result and an identical explanation. Every gate fails closed — missing
/// metrics, unloaded filters, an unknown status, or stale evidence all mean
/// "not eligible" rather than "assume it is fine".
/// </remarks>
public sealed class InstrumentEligibilityEvaluator
{
    private readonly IReadOnlyCollection<string> _allowedQuoteAssets;
    private readonly EligibilityThresholds _platformFloor;

    public InstrumentEligibilityEvaluator(
        IReadOnlyCollection<string> allowedQuoteAssets,
        EligibilityThresholds platformFloor)
    {
        ArgumentNullException.ThrowIfNull(allowedQuoteAssets);
        ArgumentNullException.ThrowIfNull(platformFloor);

        if (allowedQuoteAssets.Count == 0)
        {
            throw new ArgumentException("At least one allowed quote asset is required.", nameof(allowedQuoteAssets));
        }

        _allowedQuoteAssets = allowedQuoteAssets;
        _platformFloor = platformFloor;
    }

    public EligibilityEvaluation Evaluate(
        Instrument instrument,
        EligibilityScope scope,
        InstrumentMetrics? metrics,
        EligibilityThresholds configuredThresholds,
        EligibilityInputs inputs,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(configuredThresholds);
        ArgumentNullException.ThrowIfNull(inputs);

        if (scope.Interval == CandleInterval.None || scope.Purpose == EligibilityPurpose.None)
        {
            throw new ArgumentException("A concrete purpose and interval are required.", nameof(scope));
        }

        // Operator configuration may only tighten the mandatory floor.
        var thresholds = configuredThresholds.ConstrainedBy(_platformFloor);

        var results = new List<EligibilityGateResult>
        {
            EvaluateAssetClass(instrument),
            EvaluateQuoteAsset(instrument),
            EvaluateExchangeStatus(instrument),
            EvaluatePermissions(instrument),
            EvaluateFilters(instrument),
            EvaluateDataHealth(metrics, thresholds, nowUtc),
            EvaluateHistory(inputs, thresholds),
            EvaluateLiquidity(metrics, thresholds),
            EvaluateSpread(metrics, thresholds),
            EvaluateSlippage(metrics, thresholds),
            EvaluateListingAge(instrument, thresholds, nowUtc),
            EvaluateStrategyApproval(inputs)
        };

        return new EligibilityEvaluation(instrument.Id, scope, nowUtc, results);
    }

    private static EligibilityGateResult EvaluateAssetClass(Instrument instrument) =>
        instrument.AssetClass == AssetClass.Cryptocurrency && instrument.State != InstrumentState.Removed
            ? new EligibilityGateResult(EligibilityGate.AssetClass, true, "Cryptocurrency.")
            : new EligibilityGateResult(
                EligibilityGate.AssetClass,
                false,
                $"Asset class {instrument.AssetClass}, state {instrument.State}.");

    private EligibilityGateResult EvaluateQuoteAsset(Instrument instrument) =>
        _allowedQuoteAssets.Contains(instrument.QuoteAsset, StringComparer.OrdinalIgnoreCase)
            ? new EligibilityGateResult(EligibilityGate.QuoteAsset, true, $"Quote asset {instrument.QuoteAsset}.")
            : new EligibilityGateResult(
                EligibilityGate.QuoteAsset,
                false,
                $"Quote asset {instrument.QuoteAsset} is not on the allowlist.");

    private static EligibilityGateResult EvaluateExchangeStatus(Instrument instrument)
    {
        if (!instrument.IsPresentOnExchange)
        {
            return new EligibilityGateResult(
                EligibilityGate.ExchangeStatus, false, "Not present in the exchange catalogue.");
        }

        return instrument.IsTradingOnExchange
            ? new EligibilityGateResult(EligibilityGate.ExchangeStatus, true, "TRADING.")
            : new EligibilityGateResult(
                EligibilityGate.ExchangeStatus,
                false,
                $"Exchange status is {instrument.ExchangeStatus ?? "unknown"}.");
    }

    private static EligibilityGateResult EvaluatePermissions(Instrument instrument) =>
        instrument.HasSpotPermission
            ? new EligibilityGateResult(EligibilityGate.Permissions, true, "SPOT permission present.")
            : new EligibilityGateResult(EligibilityGate.Permissions, false, "SPOT permission missing.");

    private static EligibilityGateResult EvaluateFilters(Instrument instrument) =>
        instrument.FiltersLoaded
            ? new EligibilityGateResult(EligibilityGate.FiltersLoaded, true, "Exchange filters loaded.")
            : new EligibilityGateResult(EligibilityGate.FiltersLoaded, false, "Exchange filters are not loaded.");

    private static EligibilityGateResult EvaluateDataHealth(
        InstrumentMetrics? metrics,
        EligibilityThresholds thresholds,
        DateTimeOffset nowUtc)
    {
        if (metrics is null)
        {
            return new EligibilityGateResult(EligibilityGate.DataHealth, false, "No measurements available.");
        }

        if (metrics.IsStale(nowUtc, thresholds.MaximumEvidenceAge))
        {
            return new EligibilityGateResult(
                EligibilityGate.DataHealth,
                false,
                $"Evidence computed at {metrics.ComputedAtUtc:O} is older than the maximum evidence age.");
        }

        return metrics.IsDataHealthy
            ? new EligibilityGateResult(EligibilityGate.DataHealth, true, "No gaps or stale-data events.")
            : new EligibilityGateResult(
                EligibilityGate.DataHealth,
                false,
                $"{metrics.DataGapCount} data gap(s), {metrics.StaleEventCount} stale event(s).");
    }

    private static EligibilityGateResult EvaluateHistory(
        EligibilityInputs inputs,
        EligibilityThresholds thresholds) =>
        inputs.AvailableHistoryCandles >= thresholds.MinimumHistoryCandles
            ? new EligibilityGateResult(
                EligibilityGate.HistoryComplete,
                true,
                "Required history is present.",
                inputs.AvailableHistoryCandles,
                thresholds.MinimumHistoryCandles)
            : new EligibilityGateResult(
                EligibilityGate.HistoryComplete,
                false,
                "Required history is incomplete.",
                inputs.AvailableHistoryCandles,
                thresholds.MinimumHistoryCandles);

    private static EligibilityGateResult EvaluateLiquidity(
        InstrumentMetrics? metrics,
        EligibilityThresholds thresholds)
    {
        if (metrics is null)
        {
            return new EligibilityGateResult(EligibilityGate.Liquidity, false, "No liquidity measurements available.");
        }

        // Both measures must pass. A single spike day satisfies the rolling
        // measure while failing the median, and must not grant eligibility.
        if (metrics.RollingQuoteVolume < thresholds.MinimumRollingQuoteVolume)
        {
            return new EligibilityGateResult(
                EligibilityGate.Liquidity,
                false,
                "Rolling quote volume is below the minimum.",
                metrics.RollingQuoteVolume,
                thresholds.MinimumRollingQuoteVolume);
        }

        return metrics.MedianQuoteVolume >= thresholds.MinimumMedianQuoteVolume
            ? new EligibilityGateResult(
                EligibilityGate.Liquidity,
                true,
                "Rolling and median quote volume both pass.",
                metrics.MedianQuoteVolume,
                thresholds.MinimumMedianQuoteVolume)
            : new EligibilityGateResult(
                EligibilityGate.Liquidity,
                false,
                "Median quote volume is below the minimum; volume is carried by a spike.",
                metrics.MedianQuoteVolume,
                thresholds.MinimumMedianQuoteVolume);
    }

    private static EligibilityGateResult EvaluateSpread(
        InstrumentMetrics? metrics,
        EligibilityThresholds thresholds)
    {
        if (metrics is null)
        {
            return new EligibilityGateResult(EligibilityGate.Spread, false, "No spread measurements available.");
        }

        return metrics.AverageSpread <= thresholds.MaximumSpread
            ? new EligibilityGateResult(
                EligibilityGate.Spread, true, "Spread within the maximum.",
                metrics.AverageSpread, thresholds.MaximumSpread)
            : new EligibilityGateResult(
                EligibilityGate.Spread, false, "Spread exceeds the maximum.",
                metrics.AverageSpread, thresholds.MaximumSpread);
    }

    private static EligibilityGateResult EvaluateSlippage(
        InstrumentMetrics? metrics,
        EligibilityThresholds thresholds)
    {
        if (metrics is null)
        {
            return new EligibilityGateResult(EligibilityGate.Slippage, false, "No slippage estimate available.");
        }

        return metrics.EstimatedSlippage <= thresholds.MaximumEstimatedSlippage
            ? new EligibilityGateResult(
                EligibilityGate.Slippage, true, "Estimated slippage within the maximum.",
                metrics.EstimatedSlippage, thresholds.MaximumEstimatedSlippage)
            : new EligibilityGateResult(
                EligibilityGate.Slippage, false, "Estimated slippage exceeds the maximum.",
                metrics.EstimatedSlippage, thresholds.MaximumEstimatedSlippage);
    }

    private static EligibilityGateResult EvaluateListingAge(
        Instrument instrument,
        EligibilityThresholds thresholds,
        DateTimeOffset nowUtc)
    {
        var age = instrument.ListingAge(nowUtc);
        if (age is null)
        {
            return new EligibilityGateResult(
                EligibilityGate.ListingAge, false, "Listing age is unknown.");
        }

        return age >= thresholds.MinimumListingAge
            ? new EligibilityGateResult(
                EligibilityGate.ListingAge, true, $"Listed for {age.Value.TotalDays:F1} days.")
            : new EligibilityGateResult(
                EligibilityGate.ListingAge,
                false,
                $"Listed for {age.Value.TotalDays:F1} days, below the minimum of "
                + $"{thresholds.MinimumListingAge.TotalDays:F1} days.");
    }

    private static EligibilityGateResult EvaluateStrategyApproval(EligibilityInputs inputs) =>
        inputs.StrategyApproved
            ? new EligibilityGateResult(EligibilityGate.StrategyApproval, true, inputs.StrategyApprovalDetail)
            : new EligibilityGateResult(EligibilityGate.StrategyApproval, false, inputs.StrategyApprovalDetail);
}
