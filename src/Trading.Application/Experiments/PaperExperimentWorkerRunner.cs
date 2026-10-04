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

public enum ExperimentSignalDirection
{
    Bearish = -1,
    Neutral = 0,
    Bullish = 1
}

public sealed record ExperimentSignalCheck(
    string Id,
    ExperimentSignalDirection Direction,
    string Rationale);

public sealed record ExperimentConsensusEvidence(
    IReadOnlyList<ExperimentSignalCheck> Checks,
    int RequiredAgreement,
    bool MandatoryVeto,
    string? VetoReason)
{
    public int BullishCount => Checks.Count(check => check.Direction == ExperimentSignalDirection.Bullish);
    public int BearishCount => Checks.Count(check => check.Direction == ExperimentSignalDirection.Bearish);
    public IReadOnlyList<string> RequiredEntryChecks { get; init; } = [];
}

/// <summary>
/// A research observation only. It deliberately has no order, intent, position, or execution data.
/// </summary>
public sealed class ExperimentAnalysisResult
{
    private ExperimentAnalysisResult(
        ExperimentAnalysisOutcome outcome,
        string reason,
        decimal? value,
        ExperimentConsensusEvidence? consensus = null,
        ExperimentDecisionEvidence? evidence = null,
        PaperRegimeComponentSelection? selectedComponent = null)
    {
        Outcome = outcome;
        Reason = reason;
        Value = value;
        Consensus = consensus;
        Evidence = evidence;
        SelectedComponent = selectedComponent;
    }

    public ExperimentAnalysisOutcome Outcome { get; }
    public string Reason { get; }
    internal string FingerprintReason
    {
        get
        {
            if (Consensus is null) return Reason;
            var suffix = $" Checks: [{FormatChecks(Consensus.Checks)}]";
            return Reason.EndsWith(suffix, StringComparison.Ordinal)
                ? Reason[..^suffix.Length]
                : Reason;
        }
    }
    public decimal? Value { get; }
    public ExperimentConsensusEvidence? Consensus { get; }
    /// <summary>Present only when this result was returned by the approved runner.</summary>
    public ExperimentDecisionEvidence? Evidence { get; }
    public PaperRegimeComponentSelection? SelectedComponent { get; }

    public static ExperimentAnalysisResult Analyzed(string reason, decimal value) => new(ExperimentAnalysisOutcome.Analyzed, reason, value);
    public static ExperimentAnalysisResult NoCondition(string reason) => new(ExperimentAnalysisOutcome.NoCondition, reason, null);
    public static ExperimentAnalysisResult Blocked(string reason) => new(ExperimentAnalysisOutcome.Blocked, reason, null);

    internal ExperimentAnalysisResult Attest(ExperimentDecisionEvidence evidence) =>
        new(Outcome, Reason, Value, Consensus, evidence, SelectedComponent);

    internal ExperimentAnalysisResult WithOutcome(
        ExperimentAnalysisOutcome outcome,
        string reason,
        decimal? value) =>
        new(outcome, reason, value, Consensus, Evidence, SelectedComponent);

    internal ExperimentAnalysisResult WithSelectedComponent(PaperRegimeComponentSelection selection) =>
        new(Outcome, Reason, Value, Consensus, Evidence, selection);

    internal static ExperimentAnalysisResult FromConsensus(
        string familyId,
        IReadOnlyList<ExperimentSignalCheck> checks,
        int requiredAgreement,
        bool mandatoryVeto = false,
        string? vetoReason = null,
        IReadOnlyList<string>? requiredEntryChecks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count != 5)
            throw new ArgumentException("An approved consensus strategy must emit exactly five signal checks.", nameof(checks));
        if (requiredAgreement is < 4 or > 5)
            throw new ArgumentOutOfRangeException(nameof(requiredAgreement), "Consensus requires four or five agreeing checks.");
        if (requiredEntryChecks is not null && (requiredEntryChecks.Count != requiredEntryChecks.Distinct(StringComparer.Ordinal).Count()
            || requiredEntryChecks.Any(id => !checks.Any(check => check.Id == id))))
            throw new ArgumentException("Required entry checks must identify distinct checks in the consensus.", nameof(requiredEntryChecks));

        var evidence = new ExperimentConsensusEvidence(checks, requiredAgreement, mandatoryVeto, vetoReason)
        {
            RequiredEntryChecks = requiredEntryChecks?.ToArray() ?? []
        };
        var checkSummary = FormatChecks(checks);
        string WithChecks(string reason) =>
            reason.Length + checkSummary.Length + 11 <= 512
                ? $"{reason} Checks: [{checkSummary}]"
                : reason;
        if (mandatoryVeto)
            return new ExperimentAnalysisResult(
                ExperimentAnalysisOutcome.Blocked,
                WithChecks($"{familyId}: mandatory safety veto: {vetoReason ?? "unspecified unsafe evidence"}."),
                null,
                evidence);

        var missingBullish = evidence.RequiredEntryChecks
            .Where(id => checks.Single(check => check.Id == id).Direction != ExperimentSignalDirection.Bullish)
            .ToArray();
        var missingBearish = evidence.RequiredEntryChecks
            .Where(id => checks.Single(check => check.Id == id).Direction != ExperimentSignalDirection.Bearish)
            .ToArray();
        if (evidence.BullishCount >= requiredAgreement && missingBullish.Length == 0)
            return new ExperimentAnalysisResult(
                ExperimentAnalysisOutcome.Analyzed,
                WithChecks($"{familyId}: bullish consensus {evidence.BullishCount}/5 passed the {requiredAgreement}/5 threshold."),
                evidence.BullishCount / 5m,
                evidence);

        if (evidence.BearishCount >= requiredAgreement && missingBearish.Length == 0)
            return new ExperimentAnalysisResult(
                ExperimentAnalysisOutcome.Analyzed,
                WithChecks($"{familyId}: bearish consensus {evidence.BearishCount}/5 passed the {requiredAgreement}/5 threshold."),
                -evidence.BearishCount / 5m,
                evidence);

        return new ExperimentAnalysisResult(
            ExperimentAnalysisOutcome.NoCondition,
            WithChecks($"{familyId}: consensus did not pass (bullish {evidence.BullishCount}/5, bearish {evidence.BearishCount}/5; requires {requiredAgreement}/5)."
                + (evidence.BullishCount >= requiredAgreement
                    ? $" Missing bullish entry checks: {string.Join(", ", missingBullish)}."
                    : evidence.BearishCount >= requiredAgreement
                        ? $" Missing bearish entry checks: {string.Join(", ", missingBearish)}."
                        : "")),
            null,
            evidence);
    }

    private static string FormatChecks(IEnumerable<ExperimentSignalCheck> checks) =>
        string.Join(", ", checks.Select(check => $"{check.Id}={check.Direction}"));
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
        _evaluators.Values.Select(evaluator => evaluator.Definition)
            .GroupBy(definition => definition.FamilyId, StringComparer.Ordinal)
            .Select(group => group.MaxBy(definition => definition.Version)!)
            .ToArray();

    public static ApprovedExperimentStrategyRegistry CreatePlatformDefault() =>
        new(new IApprovedExperimentStrategyEvaluator[]
        {
            new EmaTrendContinuationExperimentAdapter(2),
            new DonchianBreakoutEnsembleExperimentAdapter(2),
            new BollingerMeanReversionExperimentAdapter(2),
            new RsiPullbackExperimentAdapter(2),
            new MacdVolumeAccelerationExperimentAdapter(2),
            new VolatilityCompressionBreakoutExperimentAdapter(2),
            new SurvivorshipAwareMomentumRotationExperimentAdapter(2),
            new RelativeStrengthPullbackRotationExperimentAdapter(2),
            new SessionConditionedBreakoutExperimentAdapter(2),
            new RegimeSwitchingEnsembleExperimentAdapter(2),
            new ThreeSwingChannelDivergenceExperimentAdapter(2),
            new EmaTrendContinuationExperimentAdapter(),
            new EmaTrendContinuationExperimentAdapter(4),
            new EmaTrendContinuationExperimentAdapter(5),
            new DonchianBreakoutEnsembleExperimentAdapter(),
            new DonchianBreakoutEnsembleExperimentAdapter(4),
            new DonchianBreakoutEnsembleExperimentAdapter(5),
            new BollingerMeanReversionExperimentAdapter(),
            new BollingerMeanReversionExperimentAdapter(4),
            new BollingerMeanReversionExperimentAdapter(5),
            new RsiPullbackExperimentAdapter(),
            new RsiPullbackExperimentAdapter(4),
            new RsiPullbackExperimentAdapter(5),
            new MacdVolumeAccelerationExperimentAdapter(),
            new MacdVolumeAccelerationExperimentAdapter(4),
            new MacdVolumeAccelerationExperimentAdapter(5),
            new VolatilityCompressionBreakoutExperimentAdapter(),
            new VolatilityCompressionBreakoutExperimentAdapter(4),
            new VolatilityCompressionBreakoutExperimentAdapter(5),
            new SurvivorshipAwareMomentumRotationExperimentAdapter(),
            new SurvivorshipAwareMomentumRotationExperimentAdapter(4),
            new RelativeStrengthPullbackRotationExperimentAdapter(3),
            new RelativeStrengthPullbackRotationExperimentAdapter(4),
            new RelativeStrengthPullbackRotationExperimentAdapter(),
            new SessionConditionedBreakoutExperimentAdapter(),
            new RegimeSwitchingEnsembleExperimentAdapter(3),
            new RegimeSwitchingEnsembleExperimentAdapter(),
            new RegimeSwitchingEnsembleExperimentAdapter(5),
            new ThreeSwingChannelDivergenceExperimentAdapter(),
            new ThreeSwingChannelDivergenceExperimentAdapter(4)
        });

    public ApprovedExperimentStrategyDefinition ResolveDefinition(string familyId, int version) =>
        _evaluators.TryGetValue((familyId, version), out var evaluator)
            ? evaluator.Definition
            : throw new ArgumentException("The strategy family/version is not in the approved registry.", nameof(version));

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
        var evaluator = _evaluators.Values
            .Where(candidate => candidate.Definition.FamilyId.Equals(familyId.Trim(), StringComparison.OrdinalIgnoreCase))
            .MaxBy(candidate => candidate.Definition.Version);
        if (evaluator is null)
            return ExperimentAnalysisResult.Blocked("The strategy is not in the approved experiment registry.");
        return evaluator.Evaluate(ExperimentStrategySeriesSet.Single(series), parametersJson);
    }

    public ExperimentAnalysisResult EvaluateProfile(
        string familyId,
        ExperimentCandleSeries regime,
        ExperimentCandleSeries signal,
        ExperimentCandleSeries execution,
        string parametersJson,
        ExperimentCandleSeries? hourlyContext = null,
        int strategyVersion = 3)
    {
        return !_evaluators.TryGetValue((familyId, strategyVersion), out var evaluator)
            ? ExperimentAnalysisResult.Blocked("The strategy is not in the approved experiment registry.")
            : evaluator.Evaluate(new ExperimentStrategySeriesSet(regime, signal, execution, hourlyContext), parametersJson);
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
    ExperimentAnalysisResult Evaluate(ExperimentStrategySeriesSet series, string parametersJson);
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
    protected Phase5BExperimentAdapter(string familyId, string schemaId, string requiredEvidence, int version)
    {
        if (version is not (2 or 3 or 4 or 5))
            throw new ArgumentOutOfRangeException(nameof(version));
        Definition = new ApprovedExperimentStrategyDefinition(
            familyId, version, schemaId, version, Fingerprint($"{familyId}|exact-approved-parameters-v{version}"),
            Fingerprint($"{familyId}|exact-consensus-rules-v{version}"));
        RequiredEvidence = requiredEvidence;
    }

    public ApprovedExperimentStrategyDefinition Definition { get; }
    private string RequiredEvidence { get; }

    public virtual ExperimentAnalysisResult Evaluate(ExperimentStrategySeriesSet series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        return ApprovedConsensusStrategyRules.Evaluate(Definition.FamilyId, series, parametersJson, Definition.Version);
    }

    public virtual ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson) =>
        Evaluate(ExperimentStrategySeriesSet.Single(series), parametersJson);

    protected static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    protected ExperimentAnalysisResult Consensus(
        IReadOnlyList<(string Id, bool Bullish, bool Bearish, string Rationale)> checks,
        int requiredAgreement = 4)
    {
        var latestIsSafe = checks.Count == 5;
        var signalChecks = checks.Select(check => new ExperimentSignalCheck(
            check.Id,
            check.Bullish ? ExperimentSignalDirection.Bullish
                : check.Bearish ? ExperimentSignalDirection.Bearish
                : ExperimentSignalDirection.Neutral,
            check.Rationale)).ToArray();
        return ExperimentAnalysisResult.FromConsensus(
            Definition.FamilyId,
            signalChecks,
            requiredAgreement,
            !latestIsSafe,
            latestIsSafe ? null : "the evaluator did not produce exactly five independent checks");
    }
}

internal sealed class EmaTrendContinuationExperimentAdapter : Phase5BExperimentAdapter
{
    public EmaTrendContinuationExperimentAdapter(int version = 3) : base("platform.ema-trend-continuation", "ema-trend-parameters", "approved regime, signal, and execution timeframe series", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 3)
            return ExperimentAnalysisResult.Blocked("EMA continuation requires at least three exact closed signal candles.");
        if (series.Candles.Count < 27)
        {
            var shortCurrent = series.Candles[^1];
            var shortPrior = series.Candles[^2];
            var oldest = series.Candles[^3];
            return Consensus(
            [
                ("short-trend", shortCurrent.Close > oldest.Close, shortCurrent.Close < oldest.Close, "Three-candle close direction."),
                ("latest-momentum", shortCurrent.Close > shortPrior.Close, shortCurrent.Close < shortPrior.Close, "Latest closed-candle momentum."),
                ("higher-low", shortCurrent.Low > shortPrior.Low, shortCurrent.High < shortPrior.High, "Latest structure direction."),
                ("candle-direction", shortCurrent.Close >= shortCurrent.Open, shortCurrent.Close < shortCurrent.Open, "Latest candle body direction."),
                ("volume-valid", shortCurrent.Volume > 0m, false, "Latest candle has positive reported volume.")
            ]);
        }
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        var priorFast = new ExponentialMovingAverageCalculator(12).Calculate(prior).Value!.Value;
        var priorSlow = new ExponentialMovingAverageCalculator(26).Calculate(prior).Value!.Value;
        var current = series.Candles[^1];
        var baselineVolume = prior.TakeLast(20).Average(candle => candle.Volume);
        return Consensus(
        [
            ("ema-order", fast > slow, fast < slow, "Fast EMA is directionally ordered against the slow EMA."),
            ("slow-slope", slow > priorSlow, slow < priorSlow, "Slow EMA slope establishes the directional regime."),
            ("price-structure", current.Close > slow, current.Close < slow, "Close remains on the directional side of slow EMA."),
            ("pullback-resumption", prior[^1].Close <= priorFast && current.Close > fast, prior[^1].Close >= priorFast && current.Close < fast, "Closed candle resumes after a fast-EMA pullback."),
            ("volume-participation", current.Volume >= baselineVolume, false, "Volume meets its rolling participation baseline.")
        ]);
    }
}
internal sealed class DonchianBreakoutEnsembleExperimentAdapter : Phase5BExperimentAdapter
{
    public DonchianBreakoutEnsembleExperimentAdapter(int version = 3) : base("platform.donchian-breakout-ensemble", "donchian-breakout-parameters", "approved regime, signal, and execution timeframe series", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 27)
            return ExperimentAnalysisResult.Blocked("Donchian breakout requires 27 exact closed signal candles.");
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var current = series.Candles[^1];
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        return Consensus(
        [
            ("short-channel", current.Close > prior.TakeLast(10).Max(candle => candle.High), current.Close < prior.TakeLast(10).Min(candle => candle.Low), "Close breaks the short channel."),
            ("medium-channel", current.Close > prior.TakeLast(15).Max(candle => candle.High), current.Close < prior.TakeLast(15).Min(candle => candle.Low), "Close breaks the medium channel."),
            ("long-channel", current.Close > prior.TakeLast(20).Max(candle => candle.High), current.Close < prior.TakeLast(20).Min(candle => candle.Low), "Close breaks the long channel."),
            ("volume", current.Volume > prior.TakeLast(20).Average(candle => candle.Volume), false, "Breakout volume exceeds its rolling baseline."),
            ("trend", fast > slow, fast < slow, "EMA structure confirms breakout direction.")
        ]);
    }
}
internal sealed class BollingerMeanReversionExperimentAdapter : Phase5BExperimentAdapter
{
    public BollingerMeanReversionExperimentAdapter(int version = 3) : base("platform.bollinger-mean-reversion", "bollinger-mean-reversion-parameters", "approved regime, signal, and execution timeframe series", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 27)
            return ExperimentAnalysisResult.Blocked("Bollinger mean reversion requires 27 exact closed signal candles.");
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var priorBands = new BollingerBandsCalculator(20).Calculate(prior).Value!.Value;
        var bands = new BollingerBandsCalculator(20).Calculate(series.Candles).Value!.Value;
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(series.Candles).Value!.Value;
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        var current = series.Candles[^1];
        return Consensus(
        [
            ("prior-band-extension", prior[^1].Close <= priorBands.Lower, prior[^1].Close >= priorBands.Upper, "Prior close extended beyond a volatility band."),
            ("band-reentry", current.Close > bands.Lower && current.Close < bands.Middle, current.Close < bands.Upper && current.Close > bands.Middle, "Current close re-enters toward the band centre."),
            ("rsi-extreme", rsi <= 40m, rsi >= 60m, "RSI confirms a directional range extreme."),
            ("reversal-candle", current.Close > current.Open, current.Close < current.Open, "Closed candle reverses direction."),
            ("ranging-regime", Math.Abs(fast - slow) / current.Close <= 0.01m, false, "EMA separation remains within the approved ranging threshold.")
        ]);
    }
}
internal sealed class RsiPullbackExperimentAdapter : Phase5BExperimentAdapter
{
    public RsiPullbackExperimentAdapter(int version = 3) : base("platform.rsi-pullback", "rsi-pullback-parameters", "approved regime, signal, and execution timeframe series", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 27)
            return ExperimentAnalysisResult.Blocked("RSI pullback requires 27 exact closed signal candles.");
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(series.Candles).Value!.Value;
        var priorRsi = new RelativeStrengthIndexCalculator(14).Calculate(prior).Value!.Value;
        var current = series.Candles[^1];
        return Consensus(
        [
            ("trend-order", fast > slow, fast < slow, "EMA order establishes the higher-direction trend proxy."),
            ("structural-side", current.Close > slow, current.Close < slow, "Price remains above structural invalidation."),
            ("pullback-zone", rsi is >= 30m and <= 50m, rsi is >= 50m and <= 70m, "RSI is within the bounded pullback zone."),
            ("rsi-turn", rsi > priorRsi, rsi < priorRsi, "RSI turns in the continuation direction."),
            ("price-resumption", current.Close > prior[^1].Close, current.Close < prior[^1].Close, "Closed price resumes in the trend direction.")
        ]);
    }
}
internal sealed class MacdVolumeAccelerationExperimentAdapter : Phase5BExperimentAdapter
{
    public MacdVolumeAccelerationExperimentAdapter(int version = 3) : base("platform.macd-volume", "macd-volume-parameters", "approved regime, signal, and execution timeframe series", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 35)
            return ExperimentAnalysisResult.Blocked("MACD volume acceleration requires 35 exact closed signal candles.");
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var macd = new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(series.Candles).Value!.Value;
        var priorMacd = new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(prior).Value!.Value;
        var trend = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        var current = series.Candles[^1];
        return Consensus(
        [
            ("macd-signal", macd.Line > macd.Signal, macd.Line < macd.Signal, "MACD line is directionally ordered against signal."),
            ("histogram-sign", macd.Histogram > 0m, macd.Histogram < 0m, "MACD histogram confirms direction."),
            ("histogram-acceleration", macd.Histogram > priorMacd.Histogram, macd.Histogram < priorMacd.Histogram, "MACD histogram accelerates directionally."),
            ("trend-average", current.Close > trend, current.Close < trend, "Price remains on the directional side of trend EMA."),
            ("volume", current.Volume > prior.TakeLast(20).Average(candle => candle.Volume), false, "Volume exceeds its rolling baseline.")
        ]);
    }
}
internal sealed class VolatilityCompressionBreakoutExperimentAdapter : Phase5BExperimentAdapter
{
    public VolatilityCompressionBreakoutExperimentAdapter(int version = 3) : base("platform.volatility-compression-breakout", "volatility-compression-breakout-parameters", "approved regime, signal, and execution timeframe series", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 27)
            return ExperimentAnalysisResult.Blocked("Volatility compression breakout requires 27 exact closed signal candles.");
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var recentRanges = prior.TakeLast(5).Select(candle => candle.High - candle.Low).ToArray();
        var baselineRanges = prior.TakeLast(20).Take(15).Select(candle => candle.High - candle.Low).ToArray();
        var current = series.Candles[^1];
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        return Consensus(
        [
            ("range-compression", recentRanges.Average() < baselineRanges.Average() * 0.75m, false, "Recent true ranges are compressed against baseline."),
            ("channel-break", current.Close > prior.TakeLast(20).Max(candle => candle.High), current.Close < prior.TakeLast(20).Min(candle => candle.Low), "Close breaks the pre-compression range."),
            ("volume-expansion", current.Volume > prior.TakeLast(20).Average(candle => candle.Volume), false, "Breakout volume expands above baseline."),
            ("trend-order", fast > slow, fast < slow, "EMA structure confirms direction."),
            ("candle-direction", current.Close > current.Open, current.Close < current.Open, "Breakout candle closes directionally.")
        ]);
    }
}
internal sealed class SurvivorshipAwareMomentumRotationExperimentAdapter : Phase5BExperimentAdapter
{
    public SurvivorshipAwareMomentumRotationExperimentAdapter(int version = 3) : base("platform.cross-sectional-momentum-rotation", "cross-sectional-momentum-parameters", "approved survivorship-aware cross-sectional universe evidence", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 31)
            return ExperimentAnalysisResult.Blocked("Cross-sectional momentum requires 31 exact closed candles and scanner universe ranking.");
        var current = series.Candles[^1];
        decimal Return(int periods) => current.Close / series.Candles[^(periods + 1)].Close - 1m;
        return Consensus(
        [
            ("short-momentum", Return(5) > 0m, Return(5) < 0m, "Five-period momentum is positive."),
            ("medium-momentum", Return(10) > 0m, Return(10) < 0m, "Ten-period momentum is positive."),
            ("long-momentum", Return(30) > 0m, Return(30) < 0m, "Thirty-period momentum is positive."),
            ("trend-persistence", series.Candles[^1].Close > series.Candles[^6].Close, series.Candles[^1].Close < series.Candles[^6].Close, "Momentum persists through the latest window."),
            ("liquidity", current.Volume > series.Candles.TakeLast(20).Average(candle => candle.Volume) * 0.5m, false, "Volume remains above the minimum rolling participation threshold.")
        ], 5);
    }
}
internal sealed class RelativeStrengthPullbackRotationExperimentAdapter : Phase5BExperimentAdapter
{
    public RelativeStrengthPullbackRotationExperimentAdapter(int version = 5) : base("platform.relative-strength-pullback-rotation", "relative-strength-pullback-parameters", "approved survivorship-aware cross-sectional universe evidence", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 31)
            return ExperimentAnalysisResult.Blocked("Relative-strength pullback requires 31 exact closed candles and scanner universe ranking.");
        var current = series.Candles[^1];
        var high = series.Candles.TakeLast(20).Max(candle => candle.High);
        var pullback = high == 0m ? 0m : (high - current.Close) / high;
        var ema = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(series.Candles).Value!.Value;
        return Consensus(
        [
            ("relative-momentum", current.Close > series.Candles[^31].Close, current.Close < series.Candles[^31].Close, "Long-window momentum remains positive."),
            ("bounded-pullback", pullback is >= 0.03m and <= 0.10m, false, "Pullback is inside the approved three-to-ten-percent band."),
            ("trend-support", current.Close > ema, current.Close < ema, "Price remains above trend support."),
            ("rsi-recovery-zone", rsi is >= 35m and <= 55m, rsi >= 65m, "RSI remains in the approved pullback recovery zone."),
            ("closed-recovery", current.Close > series.Candles[^2].Close, current.Close < series.Candles[^2].Close, "Latest closed candle turns upward.")
        ], 5);
    }
}
internal sealed class SessionConditionedBreakoutExperimentAdapter : Phase5BExperimentAdapter
{
    public SessionConditionedBreakoutExperimentAdapter(int version = 3) : base("platform.session-conditioned-breakout", "session-conditioned-breakout-parameters", "approved session profile and regime, signal, and execution timeframe series", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 27)
            return ExperimentAnalysisResult.Blocked("Session breakout requires 27 exact closed candles.");
        var current = series.Candles[^1];
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(current.CloseTimeUtc, "America/New_York");
        var inSession = local.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday
            && local.TimeOfDay >= TimeSpan.FromHours(9.5)
            && local.TimeOfDay <= TimeSpan.FromHours(16);
        return Consensus(
        [
            ("versioned-session", inSession, false, "Close belongs to the approved America/New_York cash-session profile."),
            ("range-break", current.Close > prior.TakeLast(20).Max(candle => candle.High), current.Close < prior.TakeLast(20).Min(candle => candle.Low), "Close breaks the prior session range."),
            ("trend-order", fast > slow, fast < slow, "EMA regime confirms breakout direction."),
            ("volume", current.Volume > prior.TakeLast(20).Average(candle => candle.Volume), false, "Breakout volume exceeds baseline."),
            ("candle-direction", current.Close > current.Open, current.Close < current.Open, "Breakout candle closes directionally.")
        ]);
    }
}
internal sealed class RegimeSwitchingEnsembleExperimentAdapter : Phase5BExperimentAdapter
{
    public RegimeSwitchingEnsembleExperimentAdapter(int version = 4) : base("platform.regime-switching-ensemble", "regime-switching-ensemble-parameters", "approved classifier output and component observations", version) { }

    public override ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        ArgumentNullException.ThrowIfNull(series);
        _ = parametersJson ?? throw new ArgumentNullException(nameof(parametersJson));
        if (series.Candles.Count < 35)
            return ExperimentAnalysisResult.Blocked("Regime consensus requires 35 exact closed candles.");
        var prior = series.Candles.Take(series.Candles.Count - 1).ToArray();
        var current = series.Candles[^1];
        var fast = new ExponentialMovingAverageCalculator(12).Calculate(series.Candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(26).Calculate(series.Candles).Value!.Value;
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(series.Candles).Value!.Value;
        var macd = new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(series.Candles).Value!.Value;
        return Consensus(
        [
            ("trend-component", fast > slow, fast < slow, "Trend component direction."),
            ("momentum-component", macd.Histogram > 0m, macd.Histogram < 0m, "Momentum component direction."),
            ("strength-component", rsi > 50m, rsi < 50m, "Relative-strength component direction."),
            ("price-component", current.Close > prior[^1].Close, current.Close < prior[^1].Close, "Closed-price component direction."),
            ("participation-component", current.Volume > prior.TakeLast(20).Average(candle => candle.Volume), false, "Participation component confirms the regime.")
        ], 5);
    }

}

internal sealed class ThreeSwingChannelDivergenceExperimentAdapter : Phase5BExperimentAdapter
{
    public ThreeSwingChannelDivergenceExperimentAdapter(int version = 3)
        : base(
            "platform.three-swing-channel-divergence",
            "three-swing-channel-divergence-parameters",
            "confirmed closed 5-minute swings with 1-hour and 4-hour channel context", version)
    {
    }
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
        if (!ApprovedStrategyParameters.TryNormalize(
                definition.FamilyId, worker.StrategyParameters, out var normalizedParameters, out var parameterError))
            return ExperimentAnalysisResult.Blocked(
                $"{definition.FamilyId}: saved strategy settings require review: {parameterError}");
        if (definition.FamilyId == "platform.donchian-breakout-ensemble" && definition.Version >= 5
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("donchianPlanModel") != "priorBreakRange")
            return ExperimentAnalysisResult.Blocked(
                "Donchian structural paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.bollinger-mean-reversion" && definition.Version >= 5
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("bollingerPlanModel") != "excursionMidBand")
            return ExperimentAnalysisResult.Blocked(
                "Bollinger excursion paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.rsi-pullback" && definition.Version >= 5
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("rsiPlanModel") != "pullbackSwing")
            return ExperimentAnalysisResult.Blocked(
                "RSI pullback structural paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.macd-volume" && definition.Version >= 5
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("macdPlanModel") != "crossSwing")
            return ExperimentAnalysisResult.Blocked(
                "MACD cross structural paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.ema-trend-continuation" && definition.Version >= 5
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("emaPlanModel") != "pullbackSwing")
            return ExperimentAnalysisResult.Blocked(
                "EMA pullback structural paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.volatility-compression-breakout" && definition.Version >= 5
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("compressionPlanModel") != "priorRange")
            return ExperimentAnalysisResult.Blocked(
                "Compression prior-range paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.cross-sectional-momentum-rotation" && definition.Version >= 4
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("momentumPlanModel") != "dailySwing")
            return ExperimentAnalysisResult.Blocked(
                "Momentum daily-swing paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.three-swing-channel-divergence" && definition.Version >= 4
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("threeSwingPlanModel") != "confirmedPivot")
            return ExperimentAnalysisResult.Blocked(
                "Three-swing confirmed-pivot paper protection needs owner review before new admissions.");
        if (definition.FamilyId == "platform.regime-switching-ensemble" && definition.Version >= 5
            && ApprovedStrategyParameters.Read(definition.FamilyId, normalizedParameters)
                .String("regimePlanModel") != "fourHourSwing")
            return ExperimentAnalysisResult.Blocked(
                "Ensemble four-hour swing paper protection needs owner review before new admissions.");

        var requirements = approval.Requirements;
        var timeframes = requirements?.TimeframeConfiguration;
        if (timeframes is null)
        {
            return ExperimentAnalysisResult.Blocked("Approved timeframe roles are required.");
        }

        var seriesSet = await LoadSeriesSetAsync(
            worker.MarketSymbol,
            timeframes,
            definition.FamilyId,
            definition.Version,
            asOfUtc,
            Math.Max(requirements!.MinimumClosedHistoryCandles, ApprovedConsensusStrategyProfiles.RequiredHistory),
            cancellationToken).ConfigureAwait(false);
        if (seriesSet.Series is null)
        {
            return ExperimentAnalysisResult.Blocked($"Closed candle evidence is unavailable: {seriesSet.BlockReason}.");
        }

        var result = await EvaluateSeriesAsync(
            worker.UserId,
            definition,
            evaluator,
            seriesSet.Series,
            assignment.Provenance,
            worker.StrategyParameters,
            seriesSet.Series.Execution,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == ExperimentAnalysisOutcome.Blocked
            && !result.Reason.Contains(definition.FamilyId, StringComparison.Ordinal))
        {
            result = ExperimentAnalysisResult.Blocked($"{definition.FamilyId}: {result.Reason}");
        }
        var candle = definition.FamilyId == "platform.relative-strength-pullback-rotation"
            && definition.Version >= 5
            ? seriesSet.Series.Execution.Candles[^1]
            : seriesSet.Series.Signal.Candles[^1];
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
            FingerprintAnalysis(seriesSet.Series, result)));
    }

    public async Task<ExperimentAnalysisResult> AnalyzeAcrossPaperTimeframesAsync(
        ExperimentWorker worker,
        ExperimentResearchGroupConfiguration configuration,
        ExperimentResearchGroupAssignment assignment,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        return await AnalyzeAsync(
            worker,
            configuration,
            assignment,
            asOfUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExperimentAnalysisResult> EvaluateSeriesAsync(
        Guid ownerId,
        ApprovedExperimentStrategyDefinition definition,
        IApprovedExperimentStrategyEvaluator evaluator,
        ExperimentStrategySeriesSet series,
        ExperimentResearchProvenance provenance,
        string strategyParameters,
        ExperimentCandleSeries executionSeries,
        CancellationToken cancellationToken)
    {
        if (definition.FamilyId is "platform.cross-sectional-momentum-rotation"
            or "platform.relative-strength-pullback-rotation"
            || definition.FamilyId == "platform.regime-switching-ensemble" && definition.Version >= 4)
        {
            return await _supplemental.EvaluateAsync(
                ownerId,
                definition.FamilyId,
                series.Signal,
                provenance,
                strategyParameters,
                series.Regime,
                executionSeries,
                cancellationToken).ConfigureAwait(false)
                ?? ExperimentAnalysisResult.Blocked(
                    $"{definition.FamilyId}: complete point-in-time universe evidence is unavailable.");
        }
        return evaluator.Evaluate(series, strategyParameters);
    }

    private static string FormatInterval(CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMinutes => "5m",
        CandleInterval.FifteenMinutes => "15m",
        CandleInterval.ThirtyMinutes => "30m",
        CandleInterval.OneHour => "1h",
        CandleInterval.FourHours => "4h",
        CandleInterval.OneDay => "1d",
        _ => interval.ToString()
    };

    private async Task<(ExperimentStrategySeriesSet? Series, string? BlockReason)> LoadSeriesSetAsync(
        string symbol,
        Trading.Strategies.StrategyTimeframeConfiguration timeframes,
        string familyId,
        int strategyVersion,
        DateTimeOffset asOfUtc,
        int requiredHistory,
        CancellationToken cancellationToken)
    {
        var roles = new[]
        {
            (Role: "regime", Interval: timeframes.Regime),
            (Role: "signal", Interval: timeframes.Signal),
            (Role: "execution", Interval: timeframes.Execution)
        };
        var roleIntervals = roles.Select(role => role.Interval).ToHashSet();
        var contextIntervals = ApprovedConsensusStrategyProfiles.RequiredIntervals(familyId, timeframes.Signal)
            .Where(interval => !roleIntervals.Contains(interval))
            .Select((interval, index) => (Role: $"context-{index}", Interval: interval));
        var primaryRole = familyId == "platform.relative-strength-pullback-rotation" && strategyVersion >= 5
            ? roles[2]
            : roles[1];
        var primary = await _candles.GetClosedSeriesAsync(
            new ExperimentCandleSeriesRequest(
                symbol, primaryRole.Interval,
                AlignDown(asOfUtc, TimeSpan.FromMinutes((int)primaryRole.Interval)),
                requiredHistory),
            cancellationToken).ConfigureAwait(false);
        if (!primary.IsAvailable || primary.Series is null)
            return (null, $"{primaryRole.Role} timeframe: {primary.BlockReason}");

        var decisionCloseUtc = primary.Series.Candles[^1].CloseTimeUtc;
        var reads = roles.Concat(contextIntervals)
            .Where(role => role.Role != primaryRole.Role)
            .Select(async role => (
                role.Role,
                Result: await _candles.GetClosedSeriesAsync(
                    new ExperimentCandleSeriesRequest(
                        symbol,
                        role.Interval,
                        AlignDown(decisionCloseUtc, TimeSpan.FromMinutes((int)role.Interval)),
                        requiredHistory),
                    cancellationToken).ConfigureAwait(false)))
            .ToArray();
        var completed = (await Task.WhenAll(reads).ConfigureAwait(false))
            .Append((primaryRole.Role, Result: primary)).ToArray();
        var unavailable = completed.FirstOrDefault(item =>
            !item.Result.IsAvailable || item.Result.Series is null);
        if (unavailable.Result is not null
            && (!unavailable.Result.IsAvailable || unavailable.Result.Series is null))
            return (null, $"{unavailable.Role} timeframe: {unavailable.Result.BlockReason}");
        var context = completed
            .Where(item => item.Role.StartsWith("context-", StringComparison.Ordinal))
            .Select(item => item.Result.Series)
            .FirstOrDefault();
        return (
            new ExperimentStrategySeriesSet(
                completed.Single(item => item.Role == "regime").Result.Series!,
                completed.Single(item => item.Role == "signal").Result.Series!,
                completed.Single(item => item.Role == "execution").Result.Series!,
                context),
            null);
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - value.UtcTicks % interval.Ticks, TimeSpan.Zero);

    private static string FingerprintSeries(ExperimentCandleSeries series)
    {
        var canonical = string.Join(
            '\n',
            series.Candles.Select(candle => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{candle.Symbol}|{(int)candle.Interval}|{candle.OpenTimeUtc:O}|{candle.CloseTimeUtc:O}|{candle.Open}|{candle.High}|{candle.Low}|{candle.Close}|{candle.Volume}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string FingerprintAnalysis(
        ExperimentStrategySeriesSet series,
        ExperimentAnalysisResult result)
    {
        var consensus = result.Consensus is null
            ? string.Empty
            : string.Join(
                '|',
                result.Consensus.Checks.Select(check =>
                    $"{check.Id}:{(int)check.Direction}:{check.Rationale}"))
                + $"|threshold:{result.Consensus.RequiredAgreement}"
                + $"|veto:{result.Consensus.MandatoryVeto}:{result.Consensus.VetoReason}"
                + (result.Consensus.RequiredEntryChecks.Count == 0
                    ? string.Empty
                    : $"|required:{string.Join(",", result.Consensus.RequiredEntryChecks)}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{FingerprintSeries(series.Regime)}|{FingerprintSeries(series.Signal)}|{FingerprintSeries(series.Execution)}|{(series.HourlyContext is null ? string.Empty : FingerprintSeries(series.HourlyContext))}|{consensus}|{result.SelectedComponent?.ComponentDecisionFingerprint}")));
    }
}
