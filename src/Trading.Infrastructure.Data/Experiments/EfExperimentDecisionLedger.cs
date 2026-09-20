using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Market;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>Durable compound-key ledger; duplicate retries return the immutable existing record.</summary>
public sealed class EfExperimentDecisionLedger : IExperimentDecisionLedger
{
    private readonly TradingDbContext _context;
    public EfExperimentDecisionLedger(TradingDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<(ExperimentDecisionWriteResult Result, ExperimentDecisionRecord? Record)> RecordAsync(
        Guid userId, ExperimentDecisionRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        if (userId == Guid.Empty || record.Key.UserId != userId) throw new InvalidOperationException("Decision records must be written by their owner.");
        var existing = await FindAsync(record.Key, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return (Equivalent(existing, record) ? ExperimentDecisionWriteResult.Duplicate : ExperimentDecisionWriteResult.Conflict, ToDomain(existing));
        var entity = ToPersisted(record);
        _context.ExperimentDecisionRecords.Add(entity);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (ExperimentDecisionWriteResult.Inserted, record);
        }
        catch (DbUpdateException)
        {
            _context.Entry(entity).State = EntityState.Detached;
            existing = await FindAsync(record.Key, cancellationToken).ConfigureAwait(false);
            if (existing is not null) return (Equivalent(existing, record) ? ExperimentDecisionWriteResult.Duplicate : ExperimentDecisionWriteResult.Conflict, ToDomain(existing));
            throw;
        }
    }

    public async Task<IReadOnlyList<ExperimentDecisionRecord>> ListAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || workerId == Guid.Empty) throw new ArgumentException("Owner and worker are required.");
        return (await _context.ExperimentDecisionRecords.AsNoTracking().Where(x => x.UserId == userId && x.WorkerId == workerId)
            .OrderBy(x => x.AsOfUtc).ThenBy(x => x.OpenTimeUtc).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ToDomain).ToArray();
    }

    private Task<PersistedExperimentDecisionRecord?> FindAsync(ExperimentDecisionKey key, CancellationToken token) =>
        _context.ExperimentDecisionRecords.SingleOrDefaultAsync(x => x.UserId == key.UserId && x.WorkerId == key.WorkerId
            && x.GroupConfigurationVersion == key.GroupConfigurationVersion && x.Group == (int)key.Group
            && x.StrategyId == key.StrategyId && x.StrategyVersion == key.StrategyVersion && x.StrategyFingerprint == key.StrategyFingerprint
            && x.Symbol == key.Symbol && x.Interval == (int)key.Interval && x.OpenTimeUtc == key.OpenTimeUtc
            && x.CloseTimeUtc == key.CloseTimeUtc && x.AsOfUtc == key.AsOfUtc, token);

    private static bool Equivalent(PersistedExperimentDecisionRecord existing, ExperimentDecisionRecord record) =>
        existing.Action == (int)record.Proposal.Action && existing.Reason == record.Proposal.Reason && existing.EvidenceFingerprint == record.EvidenceFingerprint;
    private static PersistedExperimentDecisionRecord ToPersisted(ExperimentDecisionRecord r) => new()
    {
        UserId = r.Key.UserId, WorkerId = r.Key.WorkerId, GroupConfigurationVersion = r.Key.GroupConfigurationVersion, Group = (int)r.Key.Group,
        StrategyId = r.Key.StrategyId, StrategyVersion = r.Key.StrategyVersion, StrategyFingerprint = r.Key.StrategyFingerprint,
        Symbol = r.Key.Symbol, Interval = (int)r.Key.Interval, OpenTimeUtc = r.Key.OpenTimeUtc, CloseTimeUtc = r.Key.CloseTimeUtc, AsOfUtc = r.Key.AsOfUtc,
        Action = (int)r.Proposal.Action, Reason = r.Proposal.Reason, EvidenceFingerprint = r.EvidenceFingerprint, RecordedAtUtc = r.RecordedAtUtc
    };
    private static ExperimentDecisionRecord ToDomain(PersistedExperimentDecisionRecord r) => new(
        new(r.UserId, r.WorkerId, r.GroupConfigurationVersion, (ExperimentResearchGroup)r.Group, r.StrategyId, r.StrategyVersion,
            r.StrategyFingerprint, r.Symbol, (CandleInterval)r.Interval, r.OpenTimeUtc, r.CloseTimeUtc, r.AsOfUtc),
        new((ExperimentProposalAction)r.Action, r.Reason), r.EvidenceFingerprint, r.RecordedAtUtc);
}
