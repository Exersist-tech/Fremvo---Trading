using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>SQL-backed, owner-scoped activation store with optimistic atomic state transitions.</summary>
public sealed class EfPaperTrainingActivationRepository : IPaperTrainingActivationRepository, IPaperTrainingActivationSource
{
    private readonly TradingDbContext _context;

    public EfPaperTrainingActivationRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<PaperTrainingActivation?> GetAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty) return null;
        var entity = await _context.PaperTrainingActivations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.OwnerUserId == ownerId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToDomain(entity);
    }

    public async Task<bool> TrySaveAsync(PaperTrainingActivation activation, PaperTrainingActivationState? expectedState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (activation.OwnerId == Guid.Empty) return false;

        if (expectedState is null)
        {
            _context.PaperTrainingActivations.Add(ToEntity(activation));
            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (DbUpdateException)
            {
                _context.ChangeTracker.Clear();
                return false;
            }
        }

        var existing = await _context.PaperTrainingActivations.SingleOrDefaultAsync(
            value => value.OwnerUserId == activation.OwnerId && value.State == (int)expectedState.Value,
            cancellationToken).ConfigureAwait(false);
        if (existing is null) return false;

        Copy(activation, existing);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyCollection<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken = default) =>
        await _context.PaperTrainingActivations.AsNoTracking()
            .Where(value => value.State == (int)PaperTrainingActivationState.Active)
            .Select(value => value.OwnerUserId).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    private static PaperTrainingActivation ToDomain(PersistedPaperTrainingActivation value) => new(
        value.OwnerUserId, (PaperTrainingActivationState)value.State,
        PaperTrainingActivationService.ApprovedSlots.Take(value.SlotCount).ToArray(),
        new(value.DurableClosedCandleSource, value.ApprovedResearchGroupsAndGates, value.WorkerRiskPolicy,
            value.PaperFillPolicy, value.OutputLedger, value.ProtectiveScheduler),
        value.ChangedAtUtc, value.ChangedBy);

    private static PersistedPaperTrainingActivation ToEntity(PaperTrainingActivation value)
    {
        var entity = new PersistedPaperTrainingActivation { OwnerUserId = value.OwnerId };
        Copy(value, entity);
        return entity;
    }

    private static void Copy(PaperTrainingActivation source, PersistedPaperTrainingActivation destination)
    {
        destination.State = (int)source.State;
        destination.SlotCount = source.Slots.Count;
        destination.DurableClosedCandleSource = source.Prerequisites.DurableClosedCandleSource;
        destination.ApprovedResearchGroupsAndGates = source.Prerequisites.ApprovedResearchGroupsAndGates;
        destination.WorkerRiskPolicy = source.Prerequisites.WorkerRiskPolicy;
        destination.PaperFillPolicy = source.Prerequisites.PaperFillPolicy;
        destination.OutputLedger = source.Prerequisites.OutputLedger;
        destination.ProtectiveScheduler = source.Prerequisites.ProtectiveScheduler;
        destination.ChangedAtUtc = source.ChangedAtUtc;
        destination.ChangedBy = source.ChangedBy;
    }
}
