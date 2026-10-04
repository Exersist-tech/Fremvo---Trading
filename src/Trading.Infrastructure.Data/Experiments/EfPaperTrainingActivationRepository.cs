using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Trading.Application.Experiments;
using Trading.Domain.Experiments;

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
        if (activation.PendingUniverseSnapshot is { } snapshot
            && (snapshot.OwnerId != activation.OwnerId
                || !activation.QualificationResults.Any(result =>
                    result.StrategyId == "platform.scanner"
                    && result.DatasetFingerprint == $"scan-run-{snapshot.SignalBoundaryUtc:O}"
                    && result.ScanMetrics?.EligiblePairs == snapshot.Members.Count)))
            throw new InvalidOperationException("Universe evidence must match its owner and successfully completed scan.");

        if (expectedState is null)
        {
            if (activation.PendingUniverseSnapshot is not null)
                await new EfPaperScanUniverseSnapshotRepository(_context)
                    .EnqueueIfNewAsync(activation.PendingUniverseSnapshot, cancellationToken).ConfigureAwait(false);
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
        if (activation.PersistenceRevision is not { } revision
            || !string.Equals(revision, Convert.ToBase64String(existing.RowVersion), StringComparison.Ordinal))
            return false;

        if (activation.PendingUniverseSnapshot is not null)
            await new EfPaperScanUniverseSnapshotRepository(_context)
                .EnqueueIfNewAsync(activation.PendingUniverseSnapshot, cancellationToken).ConfigureAwait(false);
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

    public async Task<IReadOnlyCollection<Guid>> GetProtectedOwnerIdsAsync(CancellationToken cancellationToken = default)
    {
        var owners = await GetActiveOwnerIdsAsync(cancellationToken).ConfigureAwait(false);
        var stopped = await _context.PaperTrainingActivations.AsNoTracking()
            .Where(value => (value.State == (int)PaperTrainingActivationState.Disabled
                    || value.State == (int)PaperTrainingActivationState.EmergencyStopped)
                && value.SlotCount > 0)
            .Select(value => value.OwnerUserId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var protectedOwners = new List<Guid>(owners);
        var workers = new EfExperimentWorkerRepository(_context);
        foreach (var owner in stopped)
        {
            if ((await workers.ListAsync(owner, cancellationToken).ConfigureAwait(false))
                .Any(worker => worker.PositionQuantity > 0m
                    && worker.Status is (ExperimentWorkerStatus.Running or ExperimentWorkerStatus.Paused or ExperimentWorkerStatus.Failed)))
                protectedOwners.Add(owner);
        }
        return protectedOwners;
    }

    public async Task<IReadOnlyCollection<PaperTrainingMarketSubscription>> GetActiveSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var serialized = await _context.PaperTrainingActivations.AsNoTracking()
            .Where(value => value.State == (int)PaperTrainingActivationState.Active
                || (value.State == (int)PaperTrainingActivationState.Disabled
                    || value.State == (int)PaperTrainingActivationState.EmergencyStopped)
                && value.SlotCount > 0)
            .Select(value => new { value.OwnerUserId, value.State, value.SlotsJson })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var subscriptions = new List<PaperTrainingMarketSubscription>();
        var workers = new EfExperimentWorkerRepository(_context);
        foreach (var activation in serialized)
        {
            var slots = Deserialize<PaperTrainingWorkerSlot>(activation.SlotsJson);
            if (activation.State is (int)PaperTrainingActivationState.Disabled
                or (int)PaperTrainingActivationState.EmergencyStopped)
            {
                var open = (await workers.ListAsync(activation.OwnerUserId, cancellationToken)
                    .ConfigureAwait(false))
                    .Where(worker => worker.PositionQuantity > 0m
                        && worker.Status is (ExperimentWorkerStatus.Running or ExperimentWorkerStatus.Paused or ExperimentWorkerStatus.Failed))
                    .ToArray();
                slots = slots.Where(slot => open.Any(worker =>
                    worker.StrategyId == slot.StrategyId
                    && worker.MarketSymbol.Equals(slot.Symbol, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }
            foreach (var slot in slots.Where(slot => !string.IsNullOrWhiteSpace(slot.Symbol)))
            {
                subscriptions.AddRange(ApprovedConsensusStrategyProfiles
                    .RequiredIntervals(slot.StrategyId, slot.Interval)
                    .Append(Trading.Domain.Market.CandleInterval.OneMinute)
                    .Distinct()
                    .Select(interval => new PaperTrainingMarketSubscription(slot.Symbol, interval)));
            }
        }
        return subscriptions
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
        var (assignments, strategyParameters) = DeserializeStrategyConfiguration(value.StrategyAssignmentsJson);
        return new PaperTrainingActivation(
            value.OwnerUserId, (PaperTrainingActivationState)value.State, slots,
            new(value.DurableClosedCandleSource, value.ApprovedResearchGroupsAndGates, value.WorkerRiskPolicy,
                value.PaperFillPolicy, value.OutputLedger, value.ProtectiveScheduler),
            value.ChangedAtUtc, value.ChangedBy,
            qualifications,
            assignments.Length == 0 ? null : assignments,
            strategyParameters)
        { PersistenceRevision = Convert.ToBase64String(value.RowVersion) };
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
        destination.StrategyAssignmentsJson = source.StrategyAssignments is null
            ? null
            : JsonSerializer.Serialize(new StrategyConfigurationPayload(
                source.ConfiguredStrategies.ToArray(),
                source.ConfiguredStrategyParameters));
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

    private static (PaperTrainingStrategyAssignment[] Assignments, IReadOnlyDictionary<string, string>? Parameters)
        DeserializeStrategyConfiguration(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ([], null);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
                return (JsonSerializer.Deserialize<PaperTrainingStrategyAssignment[]>(json) ?? [], null);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("The stored strategy configuration must be an object or legacy array.");

            var assignments = document.RootElement.TryGetProperty(nameof(StrategyConfigurationPayload.Assignments), out var assignmentsJson)
                ? assignmentsJson.Deserialize<PaperTrainingStrategyAssignment[]>() ?? []
                : [];
            var parameters = document.RootElement.TryGetProperty(nameof(StrategyConfigurationPayload.Parameters), out var parametersJson)
                ? parametersJson.Deserialize<Dictionary<string, string>>()
                : null;
            return (assignments, parameters);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored paper-training strategy configuration is invalid.", exception);
        }
    }

    private sealed record StrategyConfigurationPayload(
        PaperTrainingStrategyAssignment[] Assignments,
        IReadOnlyDictionary<string, string> Parameters);
}
