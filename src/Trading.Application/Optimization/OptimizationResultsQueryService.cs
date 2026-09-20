using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Optimization;

namespace Trading.Application.Optimization;

/// <summary>Read-only, owner-scoped projection of supplied completed optimization research evidence.</summary>
public sealed class OptimizationResultsQueryService
{
    public const int PageSize = 20;
    public const int CandidateLimit = 100;

    private readonly IOptimizationResearchReportSource _source;

    public OptimizationResultsQueryService(IOptimizationResearchReportSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public async Task<OptimizationResultsPage> ListAsync(Guid ownerId, int page, CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("A valid owner is required.", nameof(ownerId));
        }

        var normalizedPage = Math.Max(page, 0);
        var skip = checked(normalizedPage * PageSize);
        var records = await _source.ListForOwnerAsync(ownerId, skip, PageSize + 1, cancellationToken).ConfigureAwait(false);
        var scoped = records.Where(record => record.OwnerId == ownerId).Take(PageSize + 1).ToArray();

        return new OptimizationResultsPage(
            normalizedPage,
            scoped.Length > PageSize,
            scoped.Take(PageSize).Select(ToReport).ToArray());
    }

    private static OptimizationResearchReport ToReport(OptimizationResearchReportEnvelope record)
    {
        var result = record.Result;
        var candidates = result.RankedCandidates
            .OrderByDescending(candidate => candidate.SelectionScore)
            .ThenBy(StableParameterIdentity, StringComparer.Ordinal)
            .Take(CandidateLimit)
            .Select((candidate, index) => new OptimizationCandidateReport(
                index + 1,
                Parameters(candidate.Parameters),
                Decimal(candidate.SelectionScore)))
            .ToArray();
        var folds = result.WalkForward?.Folds
            .OrderBy(fold => fold.ValidationSplit.FromUtc)
            .ThenBy(fold => fold.ValidationSplit.ToUtc)
            .ThenBy(fold => fold.Id, StringComparer.Ordinal)
            .Select(fold => new WalkForwardFoldReport(
                fold.Id,
                Utc(fold.TrainingSplit.FromUtc),
                Utc(fold.TrainingSplit.ToUtc),
                Utc(fold.ValidationSplit.FromUtc),
                Utc(fold.ValidationSplit.ToUtc),
                Parameters(fold.Parameters),
                Decimal(fold.ObjectiveValue)))
            .ToArray() ?? Array.Empty<WalkForwardFoldReport>();

        return new OptimizationResearchReport(
            record.Id,
            record.ResearchId,
            Utc(record.CompletedAtUtc),
            record.Provenance,
            Split(record.Training),
            Split(record.Validation),
            Split(record.Holdout),
            result.RankedCandidates.Count,
            candidates,
            result.RankedCandidates.Count > candidates.Length,
            record.HoldoutVerification is null ? null : new HoldoutVerificationReport(
                "Recorded once after selection was final; it was not used to rank candidates.",
                Decimal(record.HoldoutVerification.Score),
                Utc(record.HoldoutVerification.VerifiedAtUtc)),
            folds,
            result.WalkForward is null ? null : new WalkForwardAggregateReport(
                result.WalkForward.AggregateMetrics.FoldCount,
                Decimal(result.WalkForward.AggregateMetrics.AverageObjective),
                Decimal(result.WalkForward.AggregateMetrics.MinimumObjective),
                Decimal(result.WalkForward.AggregateMetrics.MaximumObjective)));
    }

    private static DatasetSplitReport Split(DatasetSplit split) => new(
        split.SplitType.ToString(), split.DatasetId, split.DatasetVersionIdentity, split.Source, split.Symbol,
        split.Interval, Utc(split.FromUtc), Utc(split.ToUtc), split.CandleCount);

    private static OptimizationParameterReport[] Parameters(Trading.Strategies.StrategyParameterSet parameters) =>
        parameters.Values.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new OptimizationParameterReport(value.Key, Decimal(value.Value))).ToArray();

    private static string StableParameterIdentity(ParameterSearchCandidate candidate) => string.Join(
        "\u001f", candidate.Parameters.Values.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => $"{value.Key}\u001e{value.Value.ToString("G29", CultureInfo.InvariantCulture)}"));

    private static string Decimal(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

public interface IOptimizationResearchReportSource
{
    Task<IReadOnlyList<OptimizationResearchReportEnvelope>> ListForOwnerAsync(Guid ownerId, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>Trusted immutable research evidence; constructing it never runs an optimization.</summary>
public sealed class OptimizationResearchReportEnvelope
{
    public OptimizationResearchReportEnvelope(Guid id, Guid? ownerId, string researchId, OptimizationRunResult result,
        DatasetSplit training, DatasetSplit validation, DatasetSplit holdout, OptimizationHoldoutVerification? holdoutVerification,
        DateTimeOffset completedAtUtc, string provenance)
    {
        if (id == Guid.Empty) throw new ArgumentException("A report id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(researchId)) throw new ArgumentException("A research id is required.", nameof(researchId));
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(holdout);
        if (training.SplitType != DatasetSplitType.Training || validation.SplitType != DatasetSplitType.Validation || holdout.SplitType != DatasetSplitType.Holdout)
            throw new ArgumentException("Training, validation, and holdout splits are required.");
        training.ValidateNoFutureLeakage(validation);
        validation.ValidateNoFutureLeakage(holdout);
        if (!validation.IsTimeOrderedAfter(training) || !holdout.IsTimeOrderedAfter(validation))
            throw new ArgumentException("Splits must be chronologically ordered to prevent look-ahead bias.");
        if (completedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Completion time must be UTC.", nameof(completedAtUtc));
        if (string.IsNullOrWhiteSpace(provenance)) throw new ArgumentException("Provenance is required.", nameof(provenance));

        Id = id; OwnerId = ownerId; ResearchId = researchId.Trim(); Result = result; Training = training; Validation = validation;
        Holdout = holdout; HoldoutVerification = holdoutVerification; CompletedAtUtc = completedAtUtc; Provenance = provenance.Trim();
    }

    public Guid Id { get; }
    public Guid? OwnerId { get; }
    public string ResearchId { get; }
    public OptimizationRunResult Result { get; }
    public DatasetSplit Training { get; }
    public DatasetSplit Validation { get; }
    public DatasetSplit Holdout { get; }
    public OptimizationHoldoutVerification? HoldoutVerification { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public string Provenance { get; }
}

public sealed class OptimizationHoldoutVerification
{
    public OptimizationHoldoutVerification(decimal score, DateTimeOffset verifiedAtUtc)
    {
        if (verifiedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Verification time must be UTC.", nameof(verifiedAtUtc));
        Score = score; VerifiedAtUtc = verifiedAtUtc;
    }
    public decimal Score { get; }
    public DateTimeOffset VerifiedAtUtc { get; }
}

public sealed class SuppliedOptimizationResearchReportSource : IOptimizationResearchReportSource
{
    private readonly IReadOnlyList<OptimizationResearchReportEnvelope> _records;
    public SuppliedOptimizationResearchReportSource(IEnumerable<OptimizationResearchReportEnvelope> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = new ReadOnlyCollection<OptimizationResearchReportEnvelope>(records.ToArray());
    }
    public Task<IReadOnlyList<OptimizationResearchReportEnvelope>> ListForOwnerAsync(Guid ownerId, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || skip < 0 || take is < 1 or > OptimizationResultsQueryService.PageSize + 1) throw new ArgumentOutOfRangeException(nameof(ownerId));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<OptimizationResearchReportEnvelope>>(_records.Where(record => record.OwnerId == ownerId).Skip(skip).Take(take).ToArray());
    }
}

/// <summary>Default source while no durable research report store has been explicitly supplied.</summary>
public sealed class UnavailableOptimizationResearchReportSource : IOptimizationResearchReportSource
{
    public Task<IReadOnlyList<OptimizationResearchReportEnvelope>> ListForOwnerAsync(Guid ownerId, int skip, int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OptimizationResearchReportEnvelope>>(Array.Empty<OptimizationResearchReportEnvelope>());
}

public sealed record OptimizationResultsPage(int Page, bool HasMore, IReadOnlyList<OptimizationResearchReport> Results);
public sealed record OptimizationResearchReport(Guid Id, string ResearchId, string CompletedAtUtc, string Provenance, DatasetSplitReport Training, DatasetSplitReport Validation, DatasetSplitReport Holdout, int CandidateCount, IReadOnlyList<OptimizationCandidateReport> Candidates, bool HasAdditionalCandidates, HoldoutVerificationReport? HoldoutVerification, IReadOnlyList<WalkForwardFoldReport> WalkForwardFolds, WalkForwardAggregateReport? WalkForwardAggregates);
public sealed record DatasetSplitReport(string Type, string DatasetId, string DatasetVersionIdentity, string Source, string Symbol, string Interval, string FromUtc, string ToUtc, int CandleCount);
public sealed record OptimizationCandidateReport(int Rank, IReadOnlyList<OptimizationParameterReport> Parameters, string ValidationScore);
public sealed record OptimizationParameterReport(string Name, string Value);
public sealed record HoldoutVerificationReport(string Status, string Score, string VerifiedAtUtc);
public sealed record WalkForwardFoldReport(string Id, string TrainingFromUtc, string TrainingToUtc, string ValidationFromUtc, string ValidationToUtc, IReadOnlyList<OptimizationParameterReport> Parameters, string ObjectiveValue);
public sealed record WalkForwardAggregateReport(int FoldCount, string AverageObjective, string MinimumObjective, string MaximumObjective);
