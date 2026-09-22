using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData.Experiments;
using Trading.Strategies.Approvals;
using Trading.Indicators;

namespace Trading.Application.Experiments;

public enum ExperimentAnalysisOutcome
{
    Analyzed = 0,
    NoCondition,
    Blocked
}

/// <summary>
/// A research observation only. It deliberately has no order, intent, position, or execution data.
/// </summary>
public sealed class ExperimentAnalysisResult
{
    private ExperimentAnalysisResult(ExperimentAnalysisOutcome outcome, string reason, decimal? value, ExperimentDecisionEvidence? evidence = null)
    {
        Outcome = outcome;
        Reason = reason;
        Value = value;
        Evidence = evidence;
    }

    public ExperimentAnalysisOutcome Outcome { get; }
    public string Reason { get; }
    public decimal? Value { get; }
    /// <summary>Present only when this result was returned by the approved runner.</summary>
    public ExperimentDecisionEvidence? Evidence { get; }

    public static ExperimentAnalysisResult Analyzed(string reason, decimal value) => new(ExperimentAnalysisOutcome.Analyzed, reason, value);
    public static ExperimentAnalysisResult NoCondition(string reason) => new(ExperimentAnalysisOutcome.NoCondition, reason, null);
    public static ExperimentAnalysisResult Blocked(string reason) => new(ExperimentAnalysisOutcome.Blocked, reason, null);

    internal ExperimentAnalysisResult Attest(ExperimentDecisionEvidence evidence) =>
        new(Outcome, Reason, Value, evidence);
}

/// <summary>Identifies one compiled, platform-owned research evaluator.</summary>
public sealed class ApprovedExperimentStrategyDefinition
{
    internal ApprovedExperimentStrategyDefinition(
        string familyId,
        int version,
        string parameterSchemaId,
        int parameterSchemaVersion,
        string parameterSchemaFingerprint,
        string contentFingerprint)
    {
        FamilyId = familyId;
        Version = version;
        ParameterSchemaId = parameterSchemaId;
        ParameterSchemaVersion = parameterSchemaVersion;
        ParameterSchemaFingerprint = parameterSchemaFingerprint;
        ContentFingerprint = contentFingerprint;
    }

    public string FamilyId { get; }
    public int Version { get; }
    public string ParameterSchemaId { get; }
    public int ParameterSchemaVersion { get; }
    public string ParameterSchemaFingerprint { get; }
    public string ContentFingerprint { get; }
}

/// <summary>
/// A bounded, platform-owned lookup. It has no registration API: database rows and requests can
/// select only an already compiled family/version, never provide a type, delegate, or code.
/// </summary>
public sealed class ApprovedExperimentStrategyRegistry
{
    private readonly IReadOnlyDictionary<(string FamilyId, int Version), IApprovedExperimentStrategyEvaluator> _evaluators;

    private ApprovedExperimentStrategyRegistry(IEnumerable<IApprovedExperimentStrategyEvaluator> evaluators)
    {
        _evaluators = new ReadOnlyDictionary<(string FamilyId, int Version), IApprovedExperimentStrategyEvaluator>(
            evaluators.ToDictionary(
                evaluator => (evaluator.Definition.FamilyId, evaluator.Definition.Version),
                evaluator => evaluator,
                ExperimentStrategyKeyComparer.Instance));
    }

    public IReadOnlyCollection<ApprovedExperimentStrategyDefinition> Definitions =>
        _evaluators.Values.Select(evaluator => evaluator.Definition).ToArray();

    public static ApprovedExperimentStrategyRegistry CreatePlatformDefault() =>
        new(new IApprovedExperimentStrategyEvaluator[]
        {
            new EmaTrendContinuationExperimentAdapter(),
            new DonchianBreakoutEnsembleExperimentAdapter(),
            new BollingerMeanReversionExperimentAdapter(),
            new RsiPullbackExperimentAdapter(),
            new MacdVolumeAccelerationExperimentAdapter(),
            new VolatilityCompressionBreakoutExperimentAdapter(),
            new RsiMacdConfluenceExperimentAdapter(),
            new EmaRsiTrendExperimentAdapter(),
            new BollingerMacdRecoveryExperimentAdapter(),
            new DonchianVolumeBreakoutExperimentAdapter(),
            new EmaVolumePullbackExperimentAdapter(),
            new SurvivorshipAwareMomentumRotationExperimentAdapter(),
            new RelativeStrengthPullbackRotationExperimentAdapter(),
            new SessionConditionedBreakoutExperimentAdapter(),
            new RegimeSwitchingEnsembleExperimentAdapter()
        });

    internal bool TryResolve(StrategyTemplateVersionIdentity identity, out IApprovedExperimentStrategyEvaluator? evaluator) =>
        _evaluators.TryGetValue((identity.TemplateId, identity.Version), out evaluator);

    public ExperimentAnalysisResult EvaluateHistorical(
        string familyId,
        ExperimentCandleSeries series,
        string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(familyId))
            throw new ArgumentException("A strategy family is required.", nameof(familyId));
        ArgumentNullException.ThrowIfNull(series);
        if (!_evaluators.TryGetValue((familyId.Trim(), 1), out var evaluator))
            return ExperimentAnalysisResult.Blocked("The strategy is not in the approved experiment registry.");
        return evaluator.Evaluate(series, parametersJson);
    }

    private sealed class ExperimentStrategyKeyComparer : IEqualityComparer<(string FamilyId, int Version)>
    {
        public static readonly ExperimentStrategyKeyComparer Instance = new();

        public bool Equals((string FamilyId, int Version) x, (string FamilyId, int Version) y) =>
            x.Version == y.Version && string.Equals(x.FamilyId, y.FamilyId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string FamilyId, int Version) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.FamilyId), value.Version);
    }
}

internal interface IApprovedExperimentStrategyEvaluator
{
    ApprovedExperimentStrategyDefinition Definition { get; }
    ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson);
}

/// <summary>
/// Phase 5B adapters deliberately accept only the immutable, already-admitted single candle
/// series. None manufacture universe, session, classifier, component, or additional timeframe
/// evidence; their corresponding model remains unavailable until that exact evidence is supplied
/// through a future approved source.
/// </summary>
internal abstract class Phase5BExperimentAdapter : IApprovedExperimentStrategyEvaluator
{
    protected Phase5BExperimentAdapter(string familyId, string schemaId, string requiredEvidence)
    {
        Definition = new ApprovedExperimentStrategyDefinition(
            familyId, 1, schemaId, 1, Fingerprint($"{familyId}|approved-parameters-v1"),
            Fingerprint($"{familyId}|phase-5b-adapter-v1"));
        RequiredEvidence = requiredEvidence;
    }

    public ApprovedExperimentStrategyDefinition Definition { get; }
    private string RequiredEvidence { get; }

    public virtual ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        return ExperimentAnalysisResult.Blocked(
            $"Approved {Definition.FamilyId} input is unavailable: {RequiredEvidence}. No substitute input or family is used.");
    }

    protected static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed class EmaTrendContinuationExperimentAdapter : Phase5BExperimentAdapter
{
    public EmaTrendContinuationExperimentAdapter() : base("platform.ema-trend-continuation", "ema-trend-parameters", "approved regime, signal, and execution timeframe series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 3)
            return ExperimentAnalysisResult.Blocked("EMA continuation requires three exact closed signal candles.");

        // This intentionally uses only the final three persisted, chronological closed candles.
        // No current/forming candle and no later observation is read.
        var closes = series.Candles.TakeLast(3).Select(candle => candle.Close).ToArray();
        var fast = (closes[1] * 2m + closes[0]) / 3m;
        var latest = (closes[2] * 2m + closes[1]) / 3m;
        return latest > fast && closes[2] > closes[1]
            ? ExperimentAnalysisResult.Analyzed("Approved closed-candle EMA continuation is bullish.", 1m)
            : ExperimentAnalysisResult.NoCondition("Approved closed-candle EMA continuation is not bullish.");
    }
}
internal sealed class DonchianBreakoutEnsembleExperimentAdapter : Phase5BExperimentAdapter
{
    public DonchianBreakoutEnsembleExperimentAdapter() : base("platform.donchian-breakout-ensemble", "donchian-breakout-parameters", "approved regime, signal, and execution timeframe series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 21)
            return ExperimentAnalysisResult.Blocked("Donchian breakout requires 21 exact closed signal candles.");
        var priorHigh = series.Candles.TakeLast(21).Take(20).Max(candle => candle.High);
        return series.Candles[^1].Close > priorHigh
            ? ExperimentAnalysisResult.Analyzed("Approved closed-candle Donchian breakout is bullish.", 1m)
            : ExperimentAnalysisResult.NoCondition("Approved closed-candle Donchian breakout is not bullish.");
    }
}
internal sealed class BollingerMeanReversionExperimentAdapter : Phase5BExperimentAdapter
{
    public BollingerMeanReversionExperimentAdapter() : base("platform.bollinger-mean-reversion", "bollinger-mean-reversion-parameters", "approved regime, signal, and execution timeframe series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        var bands = new BollingerBandsCalculator(20).Calculate(series.Candles);
        if (!bands.IsReady || bands.Value is null)
            return ExperimentAnalysisResult.Blocked("Bollinger mean reversion requires 20 exact closed signal candles.");
        var current = series.Candles[^1];
        return current.Close <= bands.Value.Value.Lower && current.Close > current.Open
            ? ExperimentAnalysisResult.Analyzed("Approved closed-candle Bollinger reversal is bullish.", 1m)
            : ExperimentAnalysisResult.NoCondition("Approved closed-candle Bollinger reversal is not bullish.");
    }
}
internal sealed class RsiPullbackExperimentAdapter : Phase5BExperimentAdapter
{
    public RsiPullbackExperimentAdapter() : base("platform.rsi-pullback", "rsi-pullback-parameters", "approved regime, signal, and execution timeframe series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(series.Candles);
        if (!rsi.IsReady || rsi.Value is null)
            return ExperimentAnalysisResult.Blocked("RSI pullback requires 15 exact closed signal candles.");
        return rsi.Value.Value <= 30m && series.Candles[^1].Close > series.Candles[^2].Close
            ? ExperimentAnalysisResult.Analyzed("Approved closed-candle RSI pullback is bullish.", 1m)
            : ExperimentAnalysisResult.NoCondition("Approved closed-candle RSI pullback is not bullish.");
    }
}
internal sealed class MacdVolumeAccelerationExperimentAdapter : Phase5BExperimentAdapter
{
    public MacdVolumeAccelerationExperimentAdapter() : base("platform.macd-volume", "macd-volume-parameters", "approved regime, signal, and execution timeframe series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 26)
            return ExperimentAnalysisResult.Blocked("MACD volume acceleration requires 26 exact closed signal candles.");
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles);
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles);
        if (!fast.IsReady || !slow.IsReady || fast.Value is null || slow.Value is null)
            return ExperimentAnalysisResult.Blocked("MACD inputs are unavailable from the exact closed candle series.");
        var lastTwentyVolumes = series.Candles.TakeLast(21).Take(20).Select(candle => candle.Volume).ToArray();
        return fast.Value.Value > slow.Value.Value && series.Candles[^1].Volume > lastTwentyVolumes.Average()
            ? ExperimentAnalysisResult.Analyzed("Approved closed-candle MACD-volume acceleration is bullish.", 1m)
            : ExperimentAnalysisResult.NoCondition("Approved closed-candle MACD-volume acceleration is not bullish.");
    }
}
internal sealed class VolatilityCompressionBreakoutExperimentAdapter : Phase5BExperimentAdapter
{
    public VolatilityCompressionBreakoutExperimentAdapter() : base("platform.volatility-compression-breakout", "volatility-compression-breakout-parameters", "approved regime, signal, and execution timeframe series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 21)
            return ExperimentAnalysisResult.Blocked("Volatility compression breakout requires 21 exact closed signal candles.");
        var recentRanges = series.Candles.TakeLast(5).Select(candle => candle.High - candle.Low).ToArray();
        var priorRanges = series.Candles.TakeLast(21).Take(16).Select(candle => candle.High - candle.Low).ToArray();
        var priorHigh = series.Candles.TakeLast(21).Take(20).Max(candle => candle.High);
        return recentRanges.Average() < priorRanges.Average() && series.Candles[^1].Close > priorHigh
            ? ExperimentAnalysisResult.Analyzed("Approved closed-candle volatility compression breakout is bullish.", 1m)
            : ExperimentAnalysisResult.NoCondition("Approved closed-candle volatility compression breakout is not bullish.");
    }
}
internal sealed class RsiMacdConfluenceExperimentAdapter : Phase5BExperimentAdapter
{
    public RsiMacdConfluenceExperimentAdapter() : base("platform.rsi-macd-confluence", "rsi-macd-confluence-parameters", "approved RSI and MACD closed signal series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(series.Candles);
        var macd = new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(series.Candles);
        if (!rsi.IsReady || rsi.Value is null || !macd.IsReady || macd.Value is null)
            return ExperimentAnalysisResult.Blocked("RSI-MACD confluence requires 34 exact closed signal candles.");
        var current = series.Candles[^1];
        return rsi.Value.Value is >= 40m and <= 65m
            && macd.Value.Value.Line > macd.Value.Value.Signal
            && macd.Value.Value.Histogram > 0m
            && current.Close > series.Candles[^2].Close
                ? ExperimentAnalysisResult.Analyzed("Approved closed-candle RSI and MACD confluence is bullish.", 1m)
                : ExperimentAnalysisResult.NoCondition("Approved closed-candle RSI and MACD confluence is not bullish.");
    }
}
internal sealed class EmaRsiTrendExperimentAdapter : Phase5BExperimentAdapter
{
    public EmaRsiTrendExperimentAdapter() : base("platform.ema-rsi-trend", "ema-rsi-trend-parameters", "approved EMA trend and RSI closed signal series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles);
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles);
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(series.Candles);
        if (!fast.IsReady || fast.Value is null || !slow.IsReady || slow.Value is null || !rsi.IsReady || rsi.Value is null)
            return ExperimentAnalysisResult.Blocked("EMA-RSI trend requires 26 exact closed signal candles.");
        return fast.Value.Value > slow.Value.Value
            && rsi.Value.Value is >= 50m and <= 72m
            && series.Candles[^1].Close > fast.Value.Value
                ? ExperimentAnalysisResult.Analyzed("Approved closed-candle EMA trend and RSI regime are bullish.", 1m)
                : ExperimentAnalysisResult.NoCondition("Approved closed-candle EMA trend and RSI regime are not bullish.");
    }
}
internal sealed class BollingerMacdRecoveryExperimentAdapter : Phase5BExperimentAdapter
{
    public BollingerMacdRecoveryExperimentAdapter() : base("platform.bollinger-macd-recovery", "bollinger-macd-recovery-parameters", "approved Bollinger recovery and MACD closed signal series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 35)
            return ExperimentAnalysisResult.Blocked("Bollinger-MACD recovery requires 35 exact closed signal candles.");
        var priorCandles = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var priorBands = new BollingerBandsCalculator(20).Calculate(priorCandles);
        var currentBands = new BollingerBandsCalculator(20).Calculate(series.Candles);
        var macd = new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(series.Candles);
        if (priorBands.Value is null || currentBands.Value is null || macd.Value is null)
            return ExperimentAnalysisResult.Blocked("Bollinger-MACD recovery inputs are unavailable.");
        return priorCandles[^1].Close <= priorBands.Value.Value.Middle
            && series.Candles[^1].Close > currentBands.Value.Value.Middle
            && macd.Value.Value.Histogram > 0m
                ? ExperimentAnalysisResult.Analyzed("Approved closed-candle Bollinger recovery is confirmed by MACD.", 1m)
                : ExperimentAnalysisResult.NoCondition("Approved closed-candle Bollinger-MACD recovery is not bullish.");
    }
}
internal sealed class DonchianVolumeBreakoutExperimentAdapter : Phase5BExperimentAdapter
{
    public DonchianVolumeBreakoutExperimentAdapter() : base("platform.donchian-volume-breakout", "donchian-volume-breakout-parameters", "approved Donchian and volume closed signal series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 21)
            return ExperimentAnalysisResult.Blocked("Donchian-volume breakout requires 21 exact closed signal candles.");
        var prior = series.Candles.TakeLast(21).Take(20).ToArray();
        var current = series.Candles[^1];
        return current.Close > prior.Max(candle => candle.High)
            && current.Volume > prior.Average(candle => candle.Volume)
                ? ExperimentAnalysisResult.Analyzed("Approved closed-candle Donchian breakout has volume confirmation.", 1m)
                : ExperimentAnalysisResult.NoCondition("Approved closed-candle Donchian-volume breakout is not bullish.");
    }
}
internal sealed class EmaVolumePullbackExperimentAdapter : Phase5BExperimentAdapter
{
    public EmaVolumePullbackExperimentAdapter() : base("platform.ema-volume-pullback", "ema-volume-pullback-parameters", "approved EMA pullback and volume closed signal series") { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 27)
            return ExperimentAnalysisResult.Blocked("EMA-volume pullback requires 27 exact closed signal candles.");
        var priorCandles = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var priorFast = new ExponentialMovingAverageCalculator(12).Calculate(priorCandles);
        var currentFast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles);
        var currentSlow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles);
        if (priorFast.Value is null || currentFast.Value is null || currentSlow.Value is null)
            return ExperimentAnalysisResult.Blocked("EMA-volume pullback inputs are unavailable.");
        var current = series.Candles[^1];
        return currentFast.Value.Value > currentSlow.Value.Value
            && priorCandles[^1].Close <= priorFast.Value.Value
            && current.Close > currentFast.Value.Value
            && current.Volume > priorCandles.TakeLast(20).Average(candle => candle.Volume)
                ? ExperimentAnalysisResult.Analyzed("Approved closed-candle EMA pullback resumed with volume confirmation.", 1m)
                : ExperimentAnalysisResult.NoCondition("Approved closed-candle EMA-volume pullback is not bullish.");
    }
}
internal sealed class SurvivorshipAwareMomentumRotationExperimentAdapter : Phase5BExperimentAdapter
{
    public SurvivorshipAwareMomentumRotationExperimentAdapter() : base("platform.cross-sectional-momentum-rotation", "cross-sectional-momentum-parameters", "approved survivorship-aware cross-sectional universe evidence") { }
}
internal sealed class RelativeStrengthPullbackRotationExperimentAdapter : Phase5BExperimentAdapter
{
    public RelativeStrengthPullbackRotationExperimentAdapter() : base("platform.relative-strength-pullback-rotation", "relative-strength-pullback-parameters", "approved survivorship-aware cross-sectional universe evidence") { }
}
internal sealed class SessionConditionedBreakoutExperimentAdapter : Phase5BExperimentAdapter
{
    public SessionConditionedBreakoutExperimentAdapter() : base("platform.session-conditioned-breakout", "session-conditioned-breakout-parameters", "approved session profile and regime, signal, and execution timeframe series") { }
}
internal sealed class RegimeSwitchingEnsembleExperimentAdapter : Phase5BExperimentAdapter
{
    public RegimeSwitchingEnsembleExperimentAdapter() : base("platform.regime-switching-ensemble", "regime-switching-ensemble-parameters", "approved classifier output and component observations") { }
}

/// <summary>
/// Evaluates one approved research group from safe closed-candle snapshots. It never changes a
/// worker, creates a strategy decision, or invokes pipeline, execution, exchange, or position code.
/// </summary>
public sealed class PaperExperimentWorkerRunner
{
    private readonly IExperimentCandleSeriesSource _candles;
    private readonly ApprovedExperimentStrategyRegistry _registry;
    private readonly ISupplementalExperimentEvidenceProvider _supplemental;

    public PaperExperimentWorkerRunner(
        IExperimentCandleSeriesSource candles,
        ApprovedExperimentStrategyRegistry registry,
        ISupplementalExperimentEvidenceProvider? supplemental = null)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _supplemental = supplemental ?? new UnconfiguredSupplementalExperimentEvidenceProvider();
    }

    public async Task<ExperimentAnalysisResult> AnalyzeAsync(
        ExperimentWorker worker,
        ExperimentResearchGroupConfiguration configuration,
        ExperimentResearchGroupAssignment assignment,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assignment);

        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero)
        {
            return ExperimentAnalysisResult.Blocked("A UTC as-of cutoff is required.");
        }

        if (configuration.UserId != worker.UserId
            || assignment.UserId != worker.UserId
            || assignment.Group == ExperimentResearchGroup.None
            || assignment.WorkerId != worker.Id
            || !configuration.Assignments.Any(candidate => ReferenceEquals(candidate, assignment)))
        {
            return ExperimentAnalysisResult.Blocked("Worker group provenance is missing or belongs to another worker or owner.");
        }

        var approval = assignment.Provenance.Approval;
        if (!_registry.TryResolve(approval.StrategyVersion.Identity, out var evaluator) || evaluator is null)
        {
            return ExperimentAnalysisResult.Blocked("The approved family/version has no platform evaluator.");
        }

        var definition = evaluator.Definition;
        var schema = approval.StrategyVersion.ParameterSchema;
        if (!string.Equals(worker.StrategyId, approval.StrategyVersion.Identity.TemplateId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(schema.SchemaId, definition.ParameterSchemaId, StringComparison.Ordinal)
            || schema.Version != definition.ParameterSchemaVersion
            || !string.Equals(schema.ContentFingerprint, definition.ParameterSchemaFingerprint, StringComparison.Ordinal)
            || !string.Equals(approval.StrategyVersion.ContentFingerprint, definition.ContentFingerprint, StringComparison.Ordinal)
            || !string.Equals(assignment.Provenance.ParametersFingerprint, ExperimentResearchProvenance.Fingerprint(worker.StrategyParameters), StringComparison.Ordinal))
        {
            return ExperimentAnalysisResult.Blocked("The approved version, parameter schema, or parameter fingerprint does not match.");
        }

        if (!configuration.IsRunnableFor(worker, assignment))
        {
            return ExperimentAnalysisResult.Blocked("Worker group provenance is revoked, unapproved, or rejected by its required gates.");
        }

        var requirements = approval.Requirements;
        var interval = requirements?.TimeframeConfiguration?.Signal;
        if (interval is null)
        {
            return ExperimentAnalysisResult.Blocked("Approved timeframe roles are required.");
        }

        var seriesResult = await _candles.GetClosedSeriesAsync(
            new ExperimentCandleSeriesRequest(worker.MarketSymbol, interval.Value, asOfUtc, requirements!.MinimumClosedHistoryCandles),
            cancellationToken).ConfigureAwait(false);
        if (!seriesResult.IsAvailable || seriesResult.Series is null)
        {
            return ExperimentAnalysisResult.Blocked($"Closed candle evidence is unavailable: {seriesResult.BlockReason}.");
        }

        var result = await EvaluateSeriesAsync(
            definition,
            evaluator,
            seriesResult.Series,
            assignment.Provenance,
            worker.StrategyParameters,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ExperimentAnalysisOutcome.Blocked
            && !result.Reason.Contains(definition.FamilyId, StringComparison.Ordinal))
        {
            result = ExperimentAnalysisResult.Blocked($"{definition.FamilyId}: {result.Reason}");
        }
        var candle = seriesResult.Series.Candles[^1];
        return result.Attest(new ExperimentDecisionEvidence(
            worker.UserId,
            worker.Id,
            configuration.Version,
            assignment.Group,
            approval.StrategyVersion.Identity.TemplateId,
            approval.StrategyVersion.Identity.Version,
            approval.StrategyVersion.ContentFingerprint,
            assignment.Provenance.ParametersFingerprint,
            candle.CloseTimeUtc,
            candle.Symbol,
            candle.Interval,
            candle.OpenTimeUtc,
            candle.CloseTimeUtc,
            FingerprintSeries(seriesResult.Series)));
    }

    public async Task<ExperimentAnalysisResult> AnalyzeAcrossPaperTimeframesAsync(
        ExperimentWorker worker,
        ExperimentResearchGroupConfiguration configuration,
        ExperimentResearchGroupAssignment assignment,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var primary = await AnalyzeAsync(
            worker,
            configuration,
            assignment,
            asOfUtc,
            cancellationToken).ConfigureAwait(false);
        if (primary.Evidence is null || primary.Outcome == ExperimentAnalysisOutcome.Blocked)
            return primary;

        var requirements = assignment.Provenance.Approval.Requirements;
        if (requirements is null
            || PaperTrainingAutoSelectionService.ApprovedIntervals.Any(interval =>
                !requirements.AllowedIntervals.Contains(interval)))
            return ExperimentAnalysisResult
                .Blocked("All approved paper-training timeframes are required.")
                .Attest(primary.Evidence);
        if (!_registry.TryResolve(
                assignment.Provenance.Approval.StrategyVersion.Identity,
                out var evaluator)
            || evaluator is null)
            return ExperimentAnalysisResult
                .Blocked("The approved family/version has no platform evaluator.")
                .Attest(primary.Evidence);

        var supportingOutcomes = new List<(CandleInterval Interval, ExperimentAnalysisOutcome Outcome)>();
        var contextFingerprints = new List<string> { primary.Evidence.ContextFingerprint };
        var primaryCloseUtc = primary.Evidence.AsOfUtc;
        foreach (var interval in PaperTrainingAutoSelectionService.ApprovedIntervals
            .Where(interval => interval != primary.Evidence.Interval))
        {
            var seriesResult = await _candles.GetClosedSeriesAsync(
                new ExperimentCandleSeriesRequest(
                    worker.MarketSymbol,
                    interval,
                    primaryCloseUtc,
                    requirements.MinimumClosedHistoryCandles),
                cancellationToken).ConfigureAwait(false);
            if (!seriesResult.IsAvailable || seriesResult.Series is null)
                return ExperimentAnalysisResult
                    .Blocked($"Required {FormatInterval(interval)} confirmation evidence is unavailable: {seriesResult.BlockReason}.")
                    .Attest(primary.Evidence);
            contextFingerprints.Add(FingerprintSeries(seriesResult.Series));

            var result = await EvaluateSeriesAsync(
                evaluator.Definition,
                evaluator,
                seriesResult.Series,
                assignment.Provenance,
                worker.StrategyParameters,
                cancellationToken).ConfigureAwait(false);
            if (result.Outcome == ExperimentAnalysisOutcome.Blocked)
                return ExperimentAnalysisResult
                    .Blocked($"Required {FormatInterval(interval)} confirmation was blocked: {result.Reason}")
                    .Attest(primary.Evidence);
            supportingOutcomes.Add((
                interval,
                result.Outcome == ExperimentAnalysisOutcome.Analyzed
                    || IsBullishDirectionalConfirmation(seriesResult.Series)
                        ? ExperimentAnalysisOutcome.Analyzed
                        : ExperimentAnalysisOutcome.NoCondition));
        }

        var confirmations = supportingOutcomes.Count(item =>
            item.Outcome == ExperimentAnalysisOutcome.Analyzed);
        var context = string.Join(
            ", ",
            supportingOutcomes.Select(item => $"{FormatInterval(item.Interval)}={item.Outcome}"));
        var evidence = primary.Evidence.WithContextFingerprint(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('|', contextFingerprints)))));
        return primary.Outcome == ExperimentAnalysisOutcome.Analyzed && confirmations > 0
            ? ExperimentAnalysisResult
                .Analyzed(
                    $"{primary.Reason} Multi-timeframe confirmation passed ({context}).",
                    primary.Value!.Value)
                .Attest(evidence)
            : ExperimentAnalysisResult
                .NoCondition(
                    $"Multi-timeframe confirmation did not pass: primary {FormatInterval(primary.Evidence.Interval)}={primary.Outcome}; {context}.")
                .Attest(evidence);
    }

    private static bool IsBullishDirectionalConfirmation(ExperimentCandleSeries series) =>
        series.Candles.Count >= 2
        && series.Candles[^1].Close > series.Candles[^2].Close;

    private async Task<ExperimentAnalysisResult> EvaluateSeriesAsync(
        ApprovedExperimentStrategyDefinition definition,
        IApprovedExperimentStrategyEvaluator evaluator,
        ExperimentCandleSeries series,
        ExperimentResearchProvenance provenance,
        string strategyParameters,
        CancellationToken cancellationToken) =>
        await _supplemental.EvaluateAsync(
            definition.FamilyId,
            series,
            provenance,
            cancellationToken).ConfigureAwait(false)
        ?? evaluator.Evaluate(series, strategyParameters);

    private static string FormatInterval(CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMinutes => "5m",
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.ThirtyMinutes => "30m",
        CandleInterval.OneHour => "1h",
        _ => interval.ToString()
    };

    private static string FingerprintSeries(ExperimentCandleSeries series)
    {
        var canonical = string.Join(
            '\n',
            series.Candles.Select(candle => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{candle.Symbol}|{(int)candle.Interval}|{candle.OpenTimeUtc:O}|{candle.CloseTimeUtc:O}|{candle.Open}|{candle.High}|{candle.Low}|{candle.Close}|{candle.Volume}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
