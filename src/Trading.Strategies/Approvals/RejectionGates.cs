using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Market;
using Trading.Domain.Strategies;

namespace Trading.Strategies.Approvals;

public enum RejectionGateCategory
{
    None = 0,
    ApprovalCompliance = 1,
    RequiredRequirements = 2,
    SufficientHistory = 3,
    DataQuality = 4,
    OutOfSample = 5,
    CostAndLiquidity = 6,
    StableAndReproducible = 7
}

public enum RejectionGateStatus
{
    None = 0,
    Passed = 1,
    Failed = 2,
    Unavailable = 3,
    Blocked = 4
}

public sealed class RejectionGateDefinition
{
    public RejectionGateDefinition(
        string gateId,
        int version,
        RejectionGateCategory category,
        decimal? threshold = null)
    {
        if (string.IsNullOrWhiteSpace(gateId))
        {
            throw new ArgumentException("A gate id is required.", nameof(gateId));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A gate version must be positive.");
        }

        if (category == RejectionGateCategory.None || !Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), "A defined gate category is required.");
        }

        GateId = gateId.Trim();
        Version = version;
        Category = category;
        Threshold = threshold;
        Identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{GateId}@{Version}:{threshold?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
    }

    public string GateId { get; }
    public int Version { get; }
    public RejectionGateCategory Category { get; }
    public decimal? Threshold { get; }
    public string Identity { get; }
}

public sealed class ResearchEvidenceProvenance
{
    public ResearchEvidenceProvenance(
        string sourceId,
        string artifactFingerprint,
        DateTimeOffset asOfUtc)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("A safe evidence source id is required.", nameof(sourceId));
        }

        if (string.IsNullOrWhiteSpace(artifactFingerprint))
        {
            throw new ArgumentException("An evidence artifact fingerprint is required.", nameof(artifactFingerprint));
        }

        StrategyVersion.EnsureUtc(asOfUtc, nameof(asOfUtc));
        OriginId = sourceId.Trim();
        ArtifactFingerprint = artifactFingerprint.Trim().ToUpperInvariant();
        AsOfUtc = asOfUtc;
    }

    public string OriginId { get; }
    public string ArtifactFingerprint { get; }
    public DateTimeOffset AsOfUtc { get; }
}

public sealed class StrategyResearchEvidence
{
    public StrategyResearchEvidence(
        ResearchEvidenceProvenance provenance,
        StrategyApprovalEvidence? approvalEvidence,
        bool? dataQualitySafe,
        decimal? trainingNetReturn,
        decimal? outOfSampleNetReturn,
        bool? holdoutEvaluatedExactlyOnce,
        decimal? costStressedNetReturn,
        decimal? parameterNeighbourhoodPassRate,
        decimal? walkForwardPassRate,
        string? reproducibilityFingerprint)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ValidateRate(parameterNeighbourhoodPassRate, nameof(parameterNeighbourhoodPassRate));
        ValidateRate(walkForwardPassRate, nameof(walkForwardPassRate));

        Provenance = provenance;
        ApprovalEvidence = approvalEvidence;
        DataQualitySafe = dataQualitySafe;
        TrainingNetReturn = trainingNetReturn;
        OutOfSampleNetReturn = outOfSampleNetReturn;
        HoldoutEvaluatedExactlyOnce = holdoutEvaluatedExactlyOnce;
        CostStressedNetReturn = costStressedNetReturn;
        ParameterNeighbourhoodPassRate = parameterNeighbourhoodPassRate;
        WalkForwardPassRate = walkForwardPassRate;
        ReproducibilityFingerprint = string.IsNullOrWhiteSpace(reproducibilityFingerprint)
            ? null
            : reproducibilityFingerprint.Trim().ToUpperInvariant();
    }

    public ResearchEvidenceProvenance Provenance { get; }
    public StrategyApprovalEvidence? ApprovalEvidence { get; }
    public bool? DataQualitySafe { get; }
    public decimal? TrainingNetReturn { get; }
    public decimal? OutOfSampleNetReturn { get; }
    public bool? HoldoutEvaluatedExactlyOnce { get; }
    public decimal? CostStressedNetReturn { get; }
    public decimal? ParameterNeighbourhoodPassRate { get; }
    public decimal? WalkForwardPassRate { get; }
    public string? ReproducibilityFingerprint { get; }

    private static void ValidateRate(decimal? rate, string parameterName)
    {
        if (rate is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A rate must be between zero and one.");
        }
    }
}

public sealed class StrategyRejectionGateEvaluationInput
{
    public StrategyRejectionGateEvaluationInput(
        StrategyApproval? approval,
        StrategyTimeframeConfiguration? timeframeConfiguration,
        TradingProductType productType,
        StrategyApprovalMode mode,
        StrategyResearchEvidence? evidence,
        DateTimeOffset evaluatedAtUtc)
    {
        StrategyVersion.EnsureUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        Approval = approval;
        TimeframeConfiguration = timeframeConfiguration;
        ProductType = productType;
        Mode = mode;
        Evidence = evidence;
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    public StrategyApproval? Approval { get; }
    public StrategyTimeframeConfiguration? TimeframeConfiguration { get; }
    public TradingProductType ProductType { get; }
    public StrategyApprovalMode Mode { get; }
    public StrategyResearchEvidence? Evidence { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
}

public sealed class RejectionGateResult
{
    internal RejectionGateResult(
        RejectionGateDefinition definition,
        RejectionGateStatus status,
        string detail,
        decimal? measuredValue,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("Safe explanatory detail is required.", nameof(detail));
        }

        StrategyVersion.EnsureUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        GateId = definition.GateId;
        GateVersion = definition.Version;
        GateIdentity = definition.Identity;
        Category = definition.Category;
        Status = status;
        Detail = detail;
        MeasuredValue = measuredValue;
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    public string GateId { get; }
    public int GateVersion { get; }
    public string GateIdentity { get; }
    public RejectionGateCategory Category { get; }
    public RejectionGateStatus Status { get; }
    public string Detail { get; }
    public decimal? MeasuredValue { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
}

public interface IRejectionGate
{
    RejectionGateDefinition Definition { get; }

    RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input);
}

public sealed class RejectionGateEvaluation
{
    internal RejectionGateEvaluation(IEnumerable<RejectionGateResult> results)
    {
        var copied = results.ToArray();
        Results = new ReadOnlyCollection<RejectionGateResult>(copied);
        Accepted = copied.Length > 0 && copied.All(result => result.Status == RejectionGateStatus.Passed);
    }

    public IReadOnlyList<RejectionGateResult> Results { get; }

    // This is only a research acceptance outcome; it cannot transition an approval or enable execution.
    public bool Accepted { get; }
}

public sealed class StrategyRejectionGateEngine
{
    private readonly ReadOnlyCollection<IRejectionGate> _gates;

    public StrategyRejectionGateEngine(IEnumerable<IRejectionGate> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        var copied = gates.ToArray();
        if (copied.Length == 0 || copied.Any(gate => gate is null))
        {
            throw new ArgumentException("At least one platform-defined gate is required.", nameof(gates));
        }

        if (copied.Select(gate => gate.Definition.Identity).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Gate identities must be unique.", nameof(gates));
        }

        _gates = new ReadOnlyCollection<IRejectionGate>(copied);
    }

    public IReadOnlyList<IRejectionGate> Gates => _gates;

    public RejectionGateEvaluation Evaluate(StrategyRejectionGateEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new RejectionGateEvaluation(_gates.Select(gate => gate.Evaluate(input)));
    }

    public static StrategyRejectionGateEngine CreatePlatformDefault() =>
        new(
        [
            new ApprovalComplianceGate(),
            new RequiredRequirementsGate(),
            new SufficientHistoryGate(),
            new DataQualityGate(),
            new OutOfSampleGate(),
            new CostAndLiquidityGate(),
            new StableAndReproducibleGate()
        ]);
}

internal abstract class PlatformRejectionGate : IRejectionGate
{
    protected PlatformRejectionGate(RejectionGateDefinition definition) => Definition = definition;

    public RejectionGateDefinition Definition { get; }

    public abstract RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input);

    protected RejectionGateResult Result(
        StrategyRejectionGateEvaluationInput input,
        RejectionGateStatus status,
        string detail,
        decimal? measuredValue = null) =>
        new(Definition, status, detail, measuredValue, input.EvaluatedAtUtc);

    protected static bool HasFreshProvenance(StrategyRejectionGateEvaluationInput input) =>
        input.Evidence?.Provenance is { } provenance
        && provenance.AsOfUtc <= input.EvaluatedAtUtc
        && input.Approval?.Requirements is { } requirements
        && input.EvaluatedAtUtc - provenance.AsOfUtc <= requirements.MaximumEvidenceAge;
}

internal sealed class ApprovalComplianceGate : PlatformRejectionGate
{
    public ApprovalComplianceGate() : base(new("approval-compliance", 1, RejectionGateCategory.ApprovalCompliance)) { }

    public override RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input)
    {
        if (input.Approval is null || input.Approval.Requirements is null || input.TimeframeConfiguration is null)
        {
            return Result(input, RejectionGateStatus.Blocked, "Approval requirements or timeframe roles are unavailable.");
        }

        if (input.Approval.IsTerminal)
        {
            return Result(input, RejectionGateStatus.Blocked, "A terminal approval cannot progress.");
        }

        return Result(input, RejectionGateStatus.Passed, "Immutable approval requirements and timeframe roles are present.");
    }
}

internal sealed class RequiredRequirementsGate : PlatformRejectionGate
{
    public RequiredRequirementsGate() : base(new("required-requirements", 1, RejectionGateCategory.RequiredRequirements)) { }

    public override RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input)
    {
        if (input.Approval?.Requirements is not { } requirements || input.TimeframeConfiguration is null)
        {
            return Result(input, RejectionGateStatus.Blocked, "Approval requirements or timeframe roles are unavailable.");
        }

        if (input.Evidence is null || input.Evidence.ApprovalEvidence is null || !HasFreshProvenance(input))
        {
            return Result(input, RejectionGateStatus.Unavailable, "Required approved evidence or fresh provenance is unavailable.");
        }

        var evaluation = StrategyApprovalRequirementEvaluator.Evaluate(
            requirements,
            input.Evidence.ApprovalEvidence,
            input.TimeframeConfiguration,
            input.ProductType,
            input.Mode,
            input.EvaluatedAtUtc);

        return evaluation.Allowed
            ? Result(input, RejectionGateStatus.Passed, "All explicit approval requirements are satisfied.")
            : Result(input, RejectionGateStatus.Failed, "One or more explicit approval requirements are not satisfied.");
    }
}

internal sealed class SufficientHistoryGate : PlatformRejectionGate
{
    public SufficientHistoryGate() : base(new("sufficient-history", 1, RejectionGateCategory.SufficientHistory)) { }

    public override RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input)
    {
        var requirements = input.Approval?.Requirements;
        var history = input.Evidence?.ApprovalEvidence?.ClosedHistoryCandles;
        if (requirements is null)
        {
            return Result(input, RejectionGateStatus.Blocked, "A history threshold is unavailable without approval requirements.");
        }

        if (history is null || !HasFreshProvenance(input))
        {
            return Result(input, RejectionGateStatus.Unavailable, "Closed-history evidence or fresh provenance is unavailable.");
        }

        return history >= requirements.MinimumClosedHistoryCandles
            ? Result(input, RejectionGateStatus.Passed, "Closed history satisfies the approved minimum.", history)
            : Result(input, RejectionGateStatus.Failed, "Closed history is below the approved minimum.", history);
    }
}

internal sealed class DataQualityGate : PlatformRejectionGate
{
    public DataQualityGate() : base(new("data-quality", 1, RejectionGateCategory.DataQuality)) { }

    public override RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input) =>
        input.Evidence?.DataQualitySafe switch
        {
            true when HasFreshProvenance(input) => Result(input, RejectionGateStatus.Passed, "Data quality evidence is safe and fresh."),
            false => Result(input, RejectionGateStatus.Failed, "Data quality evidence reports an unsafe dataset."),
            _ => Result(input, RejectionGateStatus.Unavailable, "Data quality evidence or fresh provenance is unavailable.")
        };
}

internal sealed class OutOfSampleGate : PlatformRejectionGate
{
    public OutOfSampleGate() : base(new("out-of-sample-holdout", 1, RejectionGateCategory.OutOfSample, 0m)) { }

    public override RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input)
    {
        var evidence = input.Evidence;
        if (evidence?.TrainingNetReturn is null || evidence.OutOfSampleNetReturn is null
            || evidence.HoldoutEvaluatedExactlyOnce is null || !HasFreshProvenance(input))
        {
            return Result(input, RejectionGateStatus.Unavailable, "Out-of-sample, holdout, or fresh provenance evidence is unavailable.");
        }

        if (evidence.HoldoutEvaluatedExactlyOnce != true || evidence.OutOfSampleNetReturn < Definition.Threshold)
        {
            return Result(input, RejectionGateStatus.Failed, "Out-of-sample holdout evidence does not meet the platform rule.", evidence.OutOfSampleNetReturn);
        }

        return Result(input, RejectionGateStatus.Passed, "Out-of-sample holdout evidence satisfies the platform rule.", evidence.OutOfSampleNetReturn);
    }
}

internal sealed class CostAndLiquidityGate : PlatformRejectionGate
{
    public CostAndLiquidityGate() : base(new("cost-liquidity-feasibility", 1, RejectionGateCategory.CostAndLiquidity, 0m)) { }

    public override RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input)
    {
        var evidence = input.Evidence;
        if (evidence?.CostStressedNetReturn is null || evidence.ApprovalEvidence is null || !HasFreshProvenance(input))
        {
            return Result(input, RejectionGateStatus.Unavailable, "Cost, liquidity, or fresh provenance evidence is unavailable.");
        }

        var requirements = input.Approval?.Requirements;
        var liquid = requirements is not null
            && evidence.ApprovalEvidence.Liquidity >= requirements.MinimumLiquidity
            && evidence.ApprovalEvidence.Spread <= requirements.MaximumSpread
            && evidence.ApprovalEvidence.EstimatedSlippage <= requirements.MaximumEstimatedSlippage;
        if (!liquid || evidence.CostStressedNetReturn < Definition.Threshold)
        {
            return Result(input, RejectionGateStatus.Failed, "Cost-stressed performance or liquidity feasibility fails.", evidence.CostStressedNetReturn);
        }

        return Result(input, RejectionGateStatus.Passed, "Cost-stressed performance and liquidity feasibility pass.", evidence.CostStressedNetReturn);
    }
}

internal sealed class StableAndReproducibleGate : PlatformRejectionGate
{
    public StableAndReproducibleGate() : base(new("stable-reproducible", 1, RejectionGateCategory.StableAndReproducible, 0.80m)) { }

    public override RejectionGateResult Evaluate(StrategyRejectionGateEvaluationInput input)
    {
        var evidence = input.Evidence;
        if (evidence?.ParameterNeighbourhoodPassRate is null || evidence.WalkForwardPassRate is null
            || string.IsNullOrWhiteSpace(evidence.ReproducibilityFingerprint) || !HasFreshProvenance(input))
        {
            return Result(input, RejectionGateStatus.Unavailable, "Stability, reproducibility, or fresh provenance evidence is unavailable.");
        }

        var measured = Math.Min(evidence.ParameterNeighbourhoodPassRate.Value, evidence.WalkForwardPassRate.Value);
        return measured >= Definition.Threshold
            ? Result(input, RejectionGateStatus.Passed, "Parameter-neighbourhood, walk-forward, and reproducibility evidence pass.", measured)
            : Result(input, RejectionGateStatus.Failed, "Parameter-neighbourhood or walk-forward stability is below the platform threshold.", measured);
    }
}
