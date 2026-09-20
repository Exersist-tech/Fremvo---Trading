using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData.Experiments;
using Trading.Strategies.Approvals;

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
    private ExperimentAnalysisResult(ExperimentAnalysisOutcome outcome, string reason, decimal? value)
    {
        Outcome = outcome;
        Reason = reason;
        Value = value;
    }

    public ExperimentAnalysisOutcome Outcome { get; }
    public string Reason { get; }
    public decimal? Value { get; }

    public static ExperimentAnalysisResult Analyzed(string reason, decimal value) => new(ExperimentAnalysisOutcome.Analyzed, reason, value);
    public static ExperimentAnalysisResult NoCondition(string reason) => new(ExperimentAnalysisOutcome.NoCondition, reason, null);
    public static ExperimentAnalysisResult Blocked(string reason) => new(ExperimentAnalysisOutcome.Blocked, reason, null);
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
        new(new IApprovedExperimentStrategyEvaluator[] { new SmaTrendExperimentEvaluator() });

    internal bool TryResolve(StrategyTemplateVersionIdentity identity, out IApprovedExperimentStrategyEvaluator? evaluator) =>
        _evaluators.TryGetValue((identity.TemplateId, identity.Version), out evaluator);

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

internal sealed class SmaTrendExperimentEvaluator : IApprovedExperimentStrategyEvaluator
{
    private const string Schema = """{"fastPeriod":"positive integer","slowPeriod":"positive integer"}""";
    private const string Content = "experiment-sma-trend-v1";

    public ApprovedExperimentStrategyDefinition Definition { get; } = new(
        "experiment-sma-trend",
        1,
        "experiment-sma-trend-parameters",
        1,
        Fingerprint(Schema),
        Fingerprint(Content));

    public ExperimentAnalysisResult Evaluate(ExperimentCandleSeries series, string parametersJson)
    {
        if (!TryParseParameters(parametersJson, out var fastPeriod, out var slowPeriod))
        {
            return ExperimentAnalysisResult.Blocked("Parameters do not exactly match the approved evaluator schema.");
        }

        if (series.Candles.Count < slowPeriod)
        {
            return ExperimentAnalysisResult.NoCondition("The approved closed-candle history is insufficient.");
        }

        var closes = series.Candles.Select(candle => candle.Close).ToArray();
        var fast = closes[^fastPeriod..].Average();
        var slow = closes[^slowPeriod..].Average();
        return fast == slow
            ? ExperimentAnalysisResult.NoCondition("The approved moving averages are equal.")
            : ExperimentAnalysisResult.Analyzed(fast > slow ? "Fast average is above slow average." : "Fast average is below slow average.", fast - slow);
    }

    private static bool TryParseParameters(string json, out int fastPeriod, out int slowPeriod)
    {
        fastPeriod = 0;
        slowPeriod = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count() != 2
                || !document.RootElement.TryGetProperty("fastPeriod", out var fast)
                || !document.RootElement.TryGetProperty("slowPeriod", out var slow)
                || fast.ValueKind != JsonValueKind.Number
                || slow.ValueKind != JsonValueKind.Number
                || !fast.TryGetInt32(out fastPeriod)
                || !slow.TryGetInt32(out slowPeriod))
            {
                return false;
            }

            return fastPeriod > 0 && slowPeriod > fastPeriod;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Evaluates one approved research group from safe closed-candle snapshots. It never changes a
/// worker, creates a strategy decision, or invokes pipeline, execution, exchange, or position code.
/// </summary>
public sealed class PaperExperimentWorkerRunner
{
    private readonly IExperimentCandleSeriesSource _candles;
    private readonly ApprovedExperimentStrategyRegistry _registry;

    public PaperExperimentWorkerRunner(
        IExperimentCandleSeriesSource candles,
        ApprovedExperimentStrategyRegistry registry)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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

        return evaluator.Evaluate(seriesResult.Series, worker.StrategyParameters);
    }
}
