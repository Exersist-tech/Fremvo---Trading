using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.Domain.Market;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>
/// Durable claim store for the experiment-to-paper boundary. The unique decision/candle key is
/// inserted before pipeline execution, making a restart fail closed instead of submitting again.
/// </summary>
public sealed class EfExperimentPaperExecutionLedger : IExperimentPaperExecutionLedger
{
    private readonly TradingDbContext _context;
    public EfExperimentPaperExecutionLedger(TradingDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<(ExperimentPaperExecutionClaimResult Result, ExperimentPaperExecutionAssociation? Association)> ClaimAsync(
        Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (userId == Guid.Empty || association.DecisionKey.UserId != userId)
            throw new InvalidOperationException("Paper execution associations must be claimed by their owner.");

        var existing = await FindAsync(association.DecisionKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return (string.Equals(existing.CorrelationId, association.CorrelationId, StringComparison.Ordinal)
                ? ExperimentPaperExecutionClaimResult.Existing
                : ExperimentPaperExecutionClaimResult.Conflict, ToDomain(existing));

        var entity = ToPersisted(association);
        _context.ExperimentPaperExecutionAssociations.Add(entity);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (ExperimentPaperExecutionClaimResult.Claimed, association);
        }
        catch (DbUpdateException)
        {
            _context.Entry(entity).State = EntityState.Detached;
            existing = await FindAsync(association.DecisionKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return (string.Equals(existing.CorrelationId, association.CorrelationId, StringComparison.Ordinal)
                    ? ExperimentPaperExecutionClaimResult.Existing
                    : ExperimentPaperExecutionClaimResult.Conflict, ToDomain(existing));
            throw;
        }
    }

    public async Task CompleteAsync(Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (association.DecisionKey.UserId != userId) throw new InvalidOperationException("Only the owner may complete a paper execution association.");
        var existing = await FindAsync(association.DecisionKey, cancellationToken).ConfigureAwait(false);
        if (existing is null || !string.Equals(existing.CorrelationId, association.CorrelationId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only an existing matching claim may be completed.");
        existing.Status = (int)association.Status;
        existing.ExecutionCommandId = association.ExecutionCommandId;
        existing.Detail = association.Detail;
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<PersistedExperimentPaperExecutionAssociation?> FindAsync(ExperimentDecisionKey key, CancellationToken token) =>
        _context.ExperimentPaperExecutionAssociations.SingleOrDefaultAsync(x => x.UserId == key.UserId && x.WorkerId == key.WorkerId
            && x.GroupConfigurationVersion == key.GroupConfigurationVersion && x.Group == (int)key.Group
            && x.StrategyId == key.StrategyId && x.StrategyVersion == key.StrategyVersion && x.StrategyFingerprint == key.StrategyFingerprint
            && x.Symbol == key.Symbol && x.Interval == (int)key.Interval && x.OpenTimeUtc == key.OpenTimeUtc
            && x.CloseTimeUtc == key.CloseTimeUtc && x.AsOfUtc == key.AsOfUtc, token);

    private static PersistedExperimentPaperExecutionAssociation ToPersisted(ExperimentPaperExecutionAssociation value) => new()
    {
        UserId = value.DecisionKey.UserId, WorkerId = value.DecisionKey.WorkerId, GroupConfigurationVersion = value.DecisionKey.GroupConfigurationVersion,
        Group = (int)value.DecisionKey.Group, StrategyId = value.DecisionKey.StrategyId, StrategyVersion = value.DecisionKey.StrategyVersion,
        StrategyFingerprint = value.DecisionKey.StrategyFingerprint, Symbol = value.DecisionKey.Symbol, Interval = (int)value.DecisionKey.Interval,
        OpenTimeUtc = value.DecisionKey.OpenTimeUtc, CloseTimeUtc = value.DecisionKey.CloseTimeUtc, AsOfUtc = value.DecisionKey.AsOfUtc,
        CorrelationId = value.CorrelationId, Status = (int)value.Status, ExecutionCommandId = value.ExecutionCommandId, Detail = value.Detail
    };

    private static ExperimentPaperExecutionAssociation ToDomain(PersistedExperimentPaperExecutionAssociation value) => new(
        new(value.UserId, value.WorkerId, value.GroupConfigurationVersion, (ExperimentResearchGroup)value.Group,
            value.StrategyId, value.StrategyVersion, value.StrategyFingerprint, value.Symbol, (CandleInterval)value.Interval,
            value.OpenTimeUtc, value.CloseTimeUtc, value.AsOfUtc),
        value.CorrelationId, (ExperimentPaperExecutionStatus)value.Status, value.ExecutionCommandId, value.Detail);
}
