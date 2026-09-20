using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Market;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>Durable immutable approved-plan evidence for paper-training protective exits.</summary>
public sealed class EfExperimentPaperPlanEvidenceRepository : IExperimentPaperPlanEvidenceRepository
{
    private readonly TradingDbContext _context;

    public EfExperimentPaperPlanEvidenceRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task SaveAsync(ExperimentPaperPlanEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.DecisionKey.UserId == Guid.Empty || evidence.DecisionKey.WorkerId == Guid.Empty
            || evidence.ProtectiveStopPrice <= 0m || evidence.RecordedAtUtc == default
            || evidence.RecordedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Approved paper-plan evidence is invalid.");

        var existing = await FindAsync(evidence.DecisionKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.ProtectiveStopPrice != evidence.ProtectiveStopPrice
                || existing.ConservativeTargetPrice != evidence.ConservativeTargetPrice)
                throw new InvalidOperationException("Approved paper-plan evidence is immutable.");
            return;
        }

        _context.ExperimentPaperPlanEvidence.Add(ToPersisted(evidence));
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            var persisted = await FindAsync(evidence.DecisionKey, cancellationToken).ConfigureAwait(false);
            if (persisted is null)
                throw;
            if (persisted.ProtectiveStopPrice != evidence.ProtectiveStopPrice
                || persisted.ConservativeTargetPrice != evidence.ConservativeTargetPrice)
                throw new InvalidOperationException("Approved paper-plan evidence is immutable.");
        }
    }

    public async Task<IReadOnlyList<ExperimentPaperPlanEvidence>> ListAsync(
        Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || workerId == Guid.Empty)
            throw new ArgumentException("Owner and worker are required.");
        return (await _context.ExperimentPaperPlanEvidence.AsNoTracking()
            .Where(value => value.UserId == userId && value.WorkerId == workerId)
            .OrderBy(value => value.AsOfUtc).ThenBy(value => value.RecordedAtUtc)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ToDomain).ToArray();
    }

    private Task<PersistedExperimentPaperPlanEvidence?> FindAsync(ExperimentDecisionKey key, CancellationToken cancellationToken) =>
        _context.ExperimentPaperPlanEvidence.SingleOrDefaultAsync(value =>
            value.UserId == key.UserId && value.WorkerId == key.WorkerId
            && value.GroupConfigurationVersion == key.GroupConfigurationVersion && value.Group == (int)key.Group
            && value.StrategyId == key.StrategyId && value.StrategyVersion == key.StrategyVersion
            && value.StrategyFingerprint == key.StrategyFingerprint && value.Symbol == key.Symbol
            && value.Interval == (int)key.Interval && value.OpenTimeUtc == key.OpenTimeUtc
            && value.CloseTimeUtc == key.CloseTimeUtc && value.AsOfUtc == key.AsOfUtc, cancellationToken);

    private static PersistedExperimentPaperPlanEvidence ToPersisted(ExperimentPaperPlanEvidence value) => new()
    {
        UserId = value.DecisionKey.UserId, WorkerId = value.DecisionKey.WorkerId,
        GroupConfigurationVersion = value.DecisionKey.GroupConfigurationVersion, Group = (int)value.DecisionKey.Group,
        StrategyId = value.DecisionKey.StrategyId, StrategyVersion = value.DecisionKey.StrategyVersion,
        StrategyFingerprint = value.DecisionKey.StrategyFingerprint, Symbol = value.DecisionKey.Symbol,
        Interval = (int)value.DecisionKey.Interval, OpenTimeUtc = value.DecisionKey.OpenTimeUtc,
        CloseTimeUtc = value.DecisionKey.CloseTimeUtc, AsOfUtc = value.DecisionKey.AsOfUtc,
        ProtectiveStopPrice = value.ProtectiveStopPrice, ConservativeTargetPrice = value.ConservativeTargetPrice,
        RecordedAtUtc = value.RecordedAtUtc
    };

    private static ExperimentPaperPlanEvidence ToDomain(PersistedExperimentPaperPlanEvidence value) => new(
        new(value.UserId, value.WorkerId, value.GroupConfigurationVersion, (ExperimentResearchGroup)value.Group,
            value.StrategyId, value.StrategyVersion, value.StrategyFingerprint, value.Symbol,
            (CandleInterval)value.Interval, value.OpenTimeUtc, value.CloseTimeUtc, value.AsOfUtc),
        value.ProtectiveStopPrice, value.ConservativeTargetPrice, value.RecordedAtUtc);
}
