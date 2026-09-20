using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Trading.Backtesting;
using Trading.Domain.Experiments;
using Trading.Domain.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.Application.Experiments;

/// <summary>Fixed research cohorts. They are labels, not performance rankings.</summary>
public enum ExperimentResearchGroup
{
    None = 0,
    A = 1,
    B = 2,
    C = 3
}

/// <summary>
/// Immutable identity of the classifier whose recorded output was used by an experiment group.
/// </summary>
public sealed class ExperimentClassifierReference
{
    public ExperimentClassifierReference(string classifierId, int version, string contentFingerprint)
    {
        if (string.IsNullOrWhiteSpace(classifierId))
        {
            throw new ArgumentException("A classifier id is required.", nameof(classifierId));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A classifier version must be positive.");
        }

        ClassifierId = classifierId.Trim();
        Version = version;
        ContentFingerprint = ValidateFingerprint(contentFingerprint, nameof(contentFingerprint));
    }

    public string ClassifierId { get; }
    public int Version { get; }
    public string ContentFingerprint { get; }

    internal static string ValidateFingerprint(string fingerprint, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 content fingerprint is required.", parameterName);
        }

        return fingerprint.Trim().ToUpperInvariant();
    }
}

/// <summary>
/// Immutable provenance required before a paper-only group can advance. It records eligibility;
/// it cannot approve, promote, rank, or broaden an approval.
/// </summary>
public sealed class ExperimentResearchProvenance
{
    public ExperimentResearchProvenance(
        StrategyApproval approval,
        string parametersFingerprint,
        HistoricalDataset dataset,
        ExperimentClassifierReference classifier,
        ResearchEvidenceProvenance evidenceProvenance,
        RejectionGateEvaluation gateEvaluation)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(evidenceProvenance);
        ArgumentNullException.ThrowIfNull(gateEvaluation);

        Approval = approval;
        ParametersFingerprint = ExperimentClassifierReference.ValidateFingerprint(
            parametersFingerprint,
            nameof(parametersFingerprint));
        Dataset = dataset;
        Classifier = classifier;
        EvidenceProvenance = evidenceProvenance;
        GateEvaluation = gateEvaluation;
    }

    public StrategyApproval Approval { get; }
    public string ParametersFingerprint { get; }
    public HistoricalDataset Dataset { get; }
    public ExperimentClassifierReference Classifier { get; }
    public ResearchEvidenceProvenance EvidenceProvenance { get; }
    public RejectionGateEvaluation GateEvaluation { get; }

    internal bool Supports(ExperimentWorker worker)
    {
        var requirements = Approval.Requirements;
        return Approval.State == StrategyApprovalState.Approved
            && requirements is not null
            && requirements.AllowedModes.Contains(StrategyApprovalMode.Paper)
            && requirements.AllowedProductTypes.Contains(TradingProductType.Spot)
            && Dataset.ContainsOnlyClosedCandles
            && string.Equals(Dataset.Symbol, worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Approval.StrategyVersion.Identity.TemplateId, worker.StrategyId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ParametersFingerprint, Fingerprint(worker.StrategyParameters), StringComparison.Ordinal)
            && EvidenceProvenance.AsOfUtc <= Dataset.CreatedAtUtc
            && string.Equals(EvidenceProvenance.ArtifactFingerprint, Dataset.ContentFingerprint, StringComparison.Ordinal)
            && GateEvaluation.Accepted;
    }

    internal static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
}

/// <summary>
/// A deterministic, owner-scoped worker assignment. The derived seed is group and worker specific,
/// so a worker's random stream cannot be shared with another worker or group.
/// </summary>
public sealed class ExperimentResearchGroupAssignment
{
    internal ExperimentResearchGroupAssignment(
        Guid userId,
        Guid workerId,
        ExperimentResearchGroup group,
        int randomSeed,
        ExperimentResearchProvenance provenance)
    {
        UserId = userId;
        WorkerId = workerId;
        Group = group;
        RandomSeed = randomSeed;
        Provenance = provenance;
    }

    public Guid UserId { get; }
    public Guid WorkerId { get; }
    public ExperimentResearchGroup Group { get; }
    public int RandomSeed { get; }
    public ExperimentResearchProvenance Provenance { get; }

    public Random CreateRandom() => new(RandomSeed);
}

/// <summary>
/// Versioned immutable A/B/C assignment for at most ten workers. Membership is derived only from
/// stable worker identity ordering (A: four, B: three, C: three), never results or profit.
/// </summary>
public sealed class ExperimentResearchGroupConfiguration
{
    private readonly ReadOnlyCollection<ExperimentResearchGroupAssignment> _assignments;

    private ExperimentResearchGroupConfiguration(Guid userId, int version, IEnumerable<ExperimentResearchGroupAssignment> assignments)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user id is required.", nameof(userId));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A configuration version must be positive.");
        }

        var copied = assignments.ToArray();
        if (copied.Length > ExperimentWorker.MaxWorkersPerUser
            || copied.Select(assignment => assignment.WorkerId).Distinct().Count() != copied.Length
            || copied.Any(assignment => assignment.UserId != userId))
        {
            throw new ArgumentException("Assignments must be unique, user-scoped, and limited to ten workers.", nameof(assignments));
        }

        UserId = userId;
        Version = version;
        _assignments = new ReadOnlyCollection<ExperimentResearchGroupAssignment>(copied);
    }

    public Guid UserId { get; }
    public int Version { get; }
    public IReadOnlyList<ExperimentResearchGroupAssignment> Assignments => _assignments;

    public static ExperimentResearchGroupConfiguration Create(
        Guid userId,
        int version,
        IEnumerable<ExperimentWorker> workers,
        ExperimentResearchProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentNullException.ThrowIfNull(provenance);
        return Create(
            userId,
            version,
            workers.Select(worker => (Worker: worker, Provenance: provenance)));
    }

    /// <summary>
    /// Creates an immutable configuration with provenance bound to each exact worker. A shared
    /// provenance is insufficient for the fixed catalog because each worker has its own approved
    /// strategy identity and parameter schema.
    /// </summary>
    public static ExperimentResearchGroupConfiguration Create(
        Guid userId,
        int version,
        IEnumerable<(ExperimentWorker Worker, ExperimentResearchProvenance Provenance)> workerProvenance)
    {
        ArgumentNullException.ThrowIfNull(workerProvenance);
        var ordered = workerProvenance
            .Select(item => (
                Worker: item.Worker ?? throw new ArgumentException("A worker is required.", nameof(workerProvenance)),
                Provenance: item.Provenance ?? throw new ArgumentException("Worker provenance is required.", nameof(workerProvenance))))
            .OrderBy(item => item.Worker.Id)
            .ToArray();
        if (ordered.Length > ExperimentWorker.MaxWorkersPerUser || ordered.Any(item => item.Worker.UserId != userId))
        {
            throw new ArgumentException("Workers must belong to the configuration user and be limited to ten.", nameof(workerProvenance));
        }

        return new ExperimentResearchGroupConfiguration(
            userId,
            version,
            ordered.Select((item, index) => new ExperimentResearchGroupAssignment(
                userId,
                item.Worker.Id,
                index < 4 ? ExperimentResearchGroup.A : index < 7 ? ExperimentResearchGroup.B : ExperimentResearchGroup.C,
                DeriveSeed(version, item.Worker.Id, item.Worker.RandomSeed, index < 4 ? ExperimentResearchGroup.A : index < 7 ? ExperimentResearchGroup.B : ExperimentResearchGroup.C),
                item.Provenance)));
    }

    internal bool IsRunnableFor(ExperimentWorker worker, ExperimentResearchGroupAssignment assignment) =>
        worker.UserId == UserId
        && assignment.UserId == UserId
        && assignment.WorkerId == worker.Id
        && assignment.Provenance.Supports(worker);

    private static int DeriveSeed(int version, Guid workerId, int workerSeed, ExperimentResearchGroup group)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{version}|{workerId:N}|{workerSeed}|{(int)group}"));
        return BitConverter.ToInt32(bytes, 0);
    }
}

/// <summary>Supplies only an already approved immutable configuration; absence means no work.</summary>
public interface IExperimentResearchGroupConfigurationSource
{
    Task<ExperimentResearchGroupConfiguration?> GetAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>Host-safe default: no configuration is approval evidence, so it schedules no work.</summary>
public sealed class UnconfiguredExperimentResearchGroupConfigurationSource : IExperimentResearchGroupConfigurationSource
{
    public Task<ExperimentResearchGroupConfiguration?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ExperimentResearchGroupConfiguration?>(null);
    }
}
