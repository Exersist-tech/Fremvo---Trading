using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Trading.Application.Experiments;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>SQL-backed, owner-scoped activation store with optimistic atomic state transitions.</summary>
public sealed class EfPaperTrainingActivationRepository :
    IPaperTrainingActivationRepository,
    IPaperTrainingActivationReader,
    IPaperTrainingActivationSource,
    IPaperTrainingSubscriptionSource
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

    public async Task<IReadOnlyCollection<PaperTrainingMarketSubscription>> GetActiveSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var serialized = await _context.PaperTrainingActivations.AsNoTracking()
            .Where(value => value.State == (int)PaperTrainingActivationState.Active)
            .Select(value => value.SlotsJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return serialized
            .SelectMany(Deserialize<PaperTrainingWorkerSlot>)
            .Where(slot => !string.IsNullOrWhiteSpace(slot.Symbol))
            .SelectMany(slot => PaperTrainingAutoSelectionService.ApprovedIntervals.Select(
                interval => new PaperTrainingMarketSubscription(slot.Symbol, interval)))
            .Distinct()
            .OrderBy(subscription => subscription.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(subscription => subscription.Interval)
            .ToArray();
    }

    private static PaperTrainingActivation ToDomain(PersistedPaperTrainingActivation value)
    {
        var slots = Deserialize<PaperTrainingWorkerSlot>(value.SlotsJson)
            .Select(slot => slot.Interval == Trading.Domain.Market.CandleInterval.None
                ? slot with { Interval = Trading.Domain.Market.CandleInterval.OneHour }
                : slot)
            .ToArray();
        if (slots.Length == 0 && value.SlotCount > 0)
            slots = PaperTrainingActivationService.ApprovedSlots.Take(value.SlotCount).ToArray();
        var qualifications = Deserialize<PaperTrainingQualificationResult>(value.QualificationsJson)
            .Select(result => result.Interval == Trading.Domain.Market.CandleInterval.None
                ? result with { Interval = Trading.Domain.Market.CandleInterval.OneHour }
                : result)
            .ToArray();
        return new(
            value.OwnerUserId, (PaperTrainingActivationState)value.State, slots,
            new(value.DurableClosedCandleSource, value.ApprovedResearchGroupsAndGates, value.WorkerRiskPolicy,
                value.PaperFillPolicy, value.OutputLedger, value.ProtectiveScheduler),
            value.ChangedAtUtc, value.ChangedBy,
            qualifications);
    }

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
        destination.SlotsJson = JsonSerializer.Serialize(source.Slots);
        destination.QualificationsJson = JsonSerializer.Serialize(source.QualificationResults);
        destination.DurableClosedCandleSource = source.Prerequisites.DurableClosedCandleSource;
        destination.ApprovedResearchGroupsAndGates = source.Prerequisites.ApprovedResearchGroupsAndGates;
        destination.WorkerRiskPolicy = source.Prerequisites.WorkerRiskPolicy;
        destination.PaperFillPolicy = source.Prerequisites.PaperFillPolicy;
        destination.OutputLedger = source.Prerequisites.OutputLedger;
        destination.ProtectiveScheduler = source.Prerequisites.ProtectiveScheduler;
        destination.ChangedAtUtc = source.ChangedAtUtc;
        destination.ChangedBy = source.ChangedBy;
    }

    private static T[] Deserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<T[]>(json) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored paper-training configuration is invalid.", exception);
        }
    }
}
