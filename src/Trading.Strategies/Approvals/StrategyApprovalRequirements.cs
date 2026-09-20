using System.Collections.ObjectModel;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;

namespace Trading.Strategies.Approvals;

public enum StrategyApprovalMode
{
    None = 0,
    Research = 1,
    Backtest = 2,
    Paper = 3,
    SpotTest = 4,
    SpotLive = 5,
    FuturesTest = 6,
    FuturesLive = 7
}

public sealed record ApprovedInstrumentScope
{
    public ApprovedInstrumentScope(AssetClass? assetClass, Guid? instrumentId)
    {
        if (assetClass is null && instrumentId is null)
        {
            throw new ArgumentException("An instrument scope must restrict an asset class or a platform instrument id.");
        }

        if (assetClass == Trading.Domain.Universe.AssetClass.Unknown)
        {
            throw new ArgumentException("Unknown asset classes cannot be approved.", nameof(assetClass));
        }

        if (instrumentId == Guid.Empty)
        {
            throw new ArgumentException("An instrument id cannot be empty.", nameof(instrumentId));
        }

        AssetClass = assetClass;
        InstrumentId = instrumentId;
    }

    public AssetClass? AssetClass { get; }
    public Guid? InstrumentId { get; }

    internal bool Matches(StrategyApprovalEvidence evidence) =>
        (AssetClass is null || AssetClass == evidence.AssetClass)
        && (InstrumentId is null || InstrumentId == evidence.InstrumentId);

    internal ApprovedInstrumentScope? Intersect(ApprovedInstrumentScope other)
    {
        if (AssetClass is not null && other.AssetClass is not null && AssetClass != other.AssetClass)
        {
            return null;
        }

        if (InstrumentId is not null && other.InstrumentId is not null && InstrumentId != other.InstrumentId)
        {
            return null;
        }

        return new ApprovedInstrumentScope(AssetClass ?? other.AssetClass, InstrumentId ?? other.InstrumentId);
    }
}

public sealed class StrategyApprovalRequirements
{
    private readonly ReadOnlyCollection<ApprovedInstrumentScope> _instrumentScopes;
    private readonly ReadOnlyCollection<CandleInterval> _allowedIntervals;
    private readonly ReadOnlyCollection<TradingProductType> _allowedProductTypes;
    private readonly ReadOnlyCollection<StrategyApprovalMode> _allowedModes;

    public StrategyApprovalRequirements(
        IEnumerable<ApprovedInstrumentScope> instrumentScopes,
        int minimumClosedHistoryCandles,
        decimal minimumLiquidity,
        decimal maximumSpread,
        decimal maximumEstimatedSlippage,
        TimeSpan maximumEvidenceAge,
        IEnumerable<CandleInterval> allowedIntervals,
        IEnumerable<TradingProductType> allowedProductTypes,
        IEnumerable<StrategyApprovalMode> allowedModes)
    {
        ArgumentNullException.ThrowIfNull(instrumentScopes);
        ArgumentNullException.ThrowIfNull(allowedIntervals);
        ArgumentNullException.ThrowIfNull(allowedProductTypes);
        ArgumentNullException.ThrowIfNull(allowedModes);

        if (minimumClosedHistoryCandles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumClosedHistoryCandles), "A positive closed-history minimum is required.");
        }

        if (minimumLiquidity <= 0m || maximumSpread <= 0m || maximumEstimatedSlippage <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLiquidity), "Liquidity, spread, and slippage bounds must be positive.");
        }

        if (maximumEvidenceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvidenceAge), "A positive evidence-age bound is required.");
        }

        _instrumentScopes = DistinctRequired(instrumentScopes, nameof(instrumentScopes));
        _allowedIntervals = DistinctRequired(allowedIntervals, nameof(allowedIntervals));
        _allowedProductTypes = DistinctRequired(allowedProductTypes, nameof(allowedProductTypes));
        _allowedModes = DistinctRequired(allowedModes, nameof(allowedModes));

        if (_allowedIntervals.Any(interval => interval == CandleInterval.None))
        {
            throw new ArgumentException("A concrete normalized interval is required.", nameof(allowedIntervals));
        }

        if (_allowedProductTypes.Any(product => product != TradingProductType.Spot))
        {
            throw new ArgumentException("Futures are blocked during the research phase.", nameof(allowedProductTypes));
        }

        if (_allowedModes.Any(mode => mode is StrategyApprovalMode.None
            or StrategyApprovalMode.SpotLive
            or StrategyApprovalMode.FuturesTest
            or StrategyApprovalMode.FuturesLive))
        {
            throw new ArgumentException("Live and futures modes are blocked during the research phase.", nameof(allowedModes));
        }

        MinimumClosedHistoryCandles = minimumClosedHistoryCandles;
        MinimumLiquidity = minimumLiquidity;
        MaximumSpread = maximumSpread;
        MaximumEstimatedSlippage = maximumEstimatedSlippage;
        MaximumEvidenceAge = maximumEvidenceAge;
    }

    public IReadOnlyList<ApprovedInstrumentScope> InstrumentScopes => _instrumentScopes;
    public int MinimumClosedHistoryCandles { get; }
    public decimal MinimumLiquidity { get; }
    public decimal MaximumSpread { get; }
    public decimal MaximumEstimatedSlippage { get; }
    public TimeSpan MaximumEvidenceAge { get; }
    public IReadOnlyList<CandleInterval> AllowedIntervals => _allowedIntervals;
    public IReadOnlyList<TradingProductType> AllowedProductTypes => _allowedProductTypes;
    public IReadOnlyList<StrategyApprovalMode> AllowedModes => _allowedModes;

    public StrategyApprovalRequirements Intersect(StrategyApprovalRequirements other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var scopes = _instrumentScopes
            .SelectMany(left => other._instrumentScopes.Select(right => left.Intersect(right)))
            .Where(scope => scope is not null)
            .Select(scope => scope!)
            .Distinct()
            .ToArray();

        return new StrategyApprovalRequirements(
            scopes,
            Math.Max(MinimumClosedHistoryCandles, other.MinimumClosedHistoryCandles),
            Math.Max(MinimumLiquidity, other.MinimumLiquidity),
            Math.Min(MaximumSpread, other.MaximumSpread),
            Math.Min(MaximumEstimatedSlippage, other.MaximumEstimatedSlippage),
            MaximumEvidenceAge < other.MaximumEvidenceAge ? MaximumEvidenceAge : other.MaximumEvidenceAge,
            _allowedIntervals.Intersect(other._allowedIntervals).ToArray(),
            _allowedProductTypes.Intersect(other._allowedProductTypes).ToArray(),
            _allowedModes.Intersect(other._allowedModes).ToArray());
    }

    private static ReadOnlyCollection<T> DistinctRequired<T>(IEnumerable<T> values, string parameterName)
    {
        var distinct = values.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            throw new ArgumentException("At least one explicit restriction is required.", parameterName);
        }

        return new ReadOnlyCollection<T>(distinct);
    }
}

public sealed class StrategyApprovalEvidence
{
    public StrategyApprovalEvidence(
        Guid instrumentId,
        AssetClass assetClass,
        int closedHistoryCandles,
        decimal? liquidity,
        decimal? spread,
        decimal? estimatedSlippage,
        DateTimeOffset? observedAtUtc)
    {
        InstrumentId = instrumentId;
        AssetClass = assetClass;
        ClosedHistoryCandles = closedHistoryCandles;
        Liquidity = liquidity;
        Spread = spread;
        EstimatedSlippage = estimatedSlippage;
        ObservedAtUtc = observedAtUtc;
    }

    public Guid InstrumentId { get; }
    public AssetClass AssetClass { get; }
    public int ClosedHistoryCandles { get; }
    public decimal? Liquidity { get; }
    public decimal? Spread { get; }
    public decimal? EstimatedSlippage { get; }
    public DateTimeOffset? ObservedAtUtc { get; }
}

public sealed class StrategyApprovalEvaluation
{
    internal StrategyApprovalEvaluation(IReadOnlyList<string> failures)
    {
        Failures = new ReadOnlyCollection<string>(failures.ToArray());
    }

    public IReadOnlyList<string> Failures { get; }
    public bool Allowed => Failures.Count == 0;
}

public static class StrategyApprovalRequirementEvaluator
{
    public static StrategyApprovalEvaluation Evaluate(
        StrategyApprovalRequirements requirements,
        StrategyApprovalEvidence? evidence,
        CandleInterval interval,
        TradingProductType productType,
        StrategyApprovalMode mode,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var failures = new List<string>();
        if (evidence is null)
        {
            failures.Add("Evidence is absent.");
            return new StrategyApprovalEvaluation(failures);
        }

        if (evidence.InstrumentId == Guid.Empty
            || evidence.AssetClass == AssetClass.Unknown
            || !requirements.InstrumentScopes.Any(scope => scope.Matches(evidence)))
        {
            failures.Add("Instrument is not explicitly restricted by the approval.");
        }

        if (evidence.ClosedHistoryCandles < requirements.MinimumClosedHistoryCandles)
        {
            failures.Add("Closed history is incomplete.");
        }

        if (evidence.Liquidity is null || evidence.Liquidity < requirements.MinimumLiquidity)
        {
            failures.Add("Liquidity is absent or below the minimum.");
        }

        if (evidence.Spread is null || evidence.Spread > requirements.MaximumSpread)
        {
            failures.Add("Spread is absent or exceeds the maximum.");
        }

        if (evidence.EstimatedSlippage is null || evidence.EstimatedSlippage > requirements.MaximumEstimatedSlippage)
        {
            failures.Add("Slippage is absent or exceeds the maximum.");
        }

        if (evidence.ObservedAtUtc is null
            || evaluatedAtUtc.Offset != TimeSpan.Zero
            || evidence.ObservedAtUtc.Value.Offset != TimeSpan.Zero
            || evidence.ObservedAtUtc.Value > evaluatedAtUtc
            || evaluatedAtUtc - evidence.ObservedAtUtc.Value > requirements.MaximumEvidenceAge)
        {
            failures.Add("Evidence is absent, invalid, or stale.");
        }

        if (!requirements.AllowedIntervals.Contains(interval))
        {
            failures.Add("Interval is not allowed.");
        }

        if (!requirements.AllowedProductTypes.Contains(productType))
        {
            failures.Add("Product type is not allowed.");
        }

        if (!requirements.AllowedModes.Contains(mode))
        {
            failures.Add("Mode is not allowed.");
        }

        return new StrategyApprovalEvaluation(failures);
    }
}
