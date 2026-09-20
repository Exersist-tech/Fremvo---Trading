using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>Owner-scoped append-only snapshot ledger with explicit duplicate/conflict outcomes.</summary>
public sealed class EfExperimentResultLedger : IExperimentResultLedger
{
    private readonly TradingDbContext _context;
    public EfExperimentResultLedger(TradingDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<(ExperimentResultWriteResult Result, ExperimentResultSnapshot? Snapshot)> AppendAsync(
        Guid ownerUserId, ExperimentResultSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (ownerUserId == Guid.Empty || snapshot.OwnerUserId != ownerUserId)
            throw new InvalidOperationException("Experiment result snapshots must be written by their owner.");

        var existing = await FindAsync(ownerUserId, snapshot.SnapshotKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return (Equivalent(existing, snapshot) ? ExperimentResultWriteResult.Duplicate : ExperimentResultWriteResult.Conflict, ToDomain(existing));

        var entity = ToPersisted(snapshot);
        _context.ExperimentResultSnapshots.Add(entity);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (ExperimentResultWriteResult.Inserted, snapshot);
        }
        catch (DbUpdateException)
        {
            _context.Entry(entity).State = EntityState.Detached;
            existing = await FindAsync(ownerUserId, snapshot.SnapshotKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return (Equivalent(existing, snapshot) ? ExperimentResultWriteResult.Duplicate : ExperimentResultWriteResult.Conflict, ToDomain(existing));
            throw;
        }
    }

    public async Task<ExperimentResultPage> ListAsync(Guid ownerUserId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerUserId));
        if (page < 0 || page > 10_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        var values = await _context.ExperimentResultSnapshots.AsNoTracking().Where(x => x.OwnerUserId == ownerUserId)
            .OrderByDescending(x => x.EvaluatedAtUtc).ThenBy(x => x.SnapshotKey)
            .Skip(page * pageSize).Take(pageSize + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = values.Count > pageSize;
        return new(values.Take(pageSize).Select(ToDomain).ToArray(), page, pageSize, hasMore);
    }

    private Task<PersistedExperimentResultSnapshot?> FindAsync(Guid owner, string key, CancellationToken token) =>
        _context.ExperimentResultSnapshots.SingleOrDefaultAsync(x => x.OwnerUserId == owner && x.SnapshotKey == key, token);

    private static bool Equivalent(PersistedExperimentResultSnapshot existing, ExperimentResultSnapshot candidate) =>
        ToDomain(existing) == candidate;

    private static PersistedExperimentResultSnapshot ToPersisted(ExperimentResultSnapshot x) => new()
    {
        OwnerUserId = x.OwnerUserId, SnapshotKey = x.SnapshotKey, WorkerId = x.Provenance.WorkerId, GroupConfigurationVersion = x.Provenance.GroupConfigurationVersion,
        Group = x.Provenance.Group, StrategyId = x.Provenance.StrategyId, StrategyVersion = x.Provenance.StrategyVersion, ParametersFingerprint = x.Provenance.ParametersFingerprint,
        DatasetFingerprint = x.Provenance.DatasetFingerprint, ClassifierVersion = x.Provenance.ClassifierVersion, GateEvidenceFingerprint = x.Provenance.GateEvidenceFingerprint,
        Seed = x.Provenance.Seed, ReproducibilityIdentity = x.Provenance.ReproducibilityIdentity, EvaluatedAtUtc = x.EvaluatedAtUtc, Equity = x.Equity, Cash = x.Cash,
        PositionQuantity = x.PositionQuantity, RealizedProfitAndLoss = x.RealizedProfitAndLoss, UnrealizedProfitAndLoss = x.UnrealizedProfitAndLoss,
        MaximumDrawdown = x.MaximumDrawdown, Fees = x.Fees, Slippage = x.Slippage, RejectedFillCount = x.RejectedFillCount,
        RejectedActionCount = x.RejectedActionCount, Exposure = x.Exposure, GateFailureCount = x.GateFailureCount
    };

    private static ExperimentResultSnapshot ToDomain(PersistedExperimentResultSnapshot x) => new(
        x.OwnerUserId, x.SnapshotKey, new(x.WorkerId, x.GroupConfigurationVersion, x.Group, x.StrategyId, x.StrategyVersion,
            x.ParametersFingerprint, x.DatasetFingerprint, x.ClassifierVersion, x.GateEvidenceFingerprint, x.Seed, x.ReproducibilityIdentity),
        x.EvaluatedAtUtc, x.Equity, x.Cash, x.PositionQuantity, x.RealizedProfitAndLoss, x.UnrealizedProfitAndLoss, x.MaximumDrawdown,
        x.Fees, x.Slippage, x.RejectedFillCount, x.RejectedActionCount, x.Exposure, x.GateFailureCount);
}
