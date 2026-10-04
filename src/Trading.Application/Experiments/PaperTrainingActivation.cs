using System.Collections.Concurrent;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Market;
using Trading.Risk;
using System.Text.Json.Serialization;

namespace Trading.Application.Experiments;

public enum PaperTrainingActivationState
{
    Inactive = 0,
    Active = 1,
    Disabled = 2,
    EmergencyStopped = 3
}

/// <summary>
/// Platform-owned worker definitions. Balances, symbols, strategies, and seeds are intentionally
/// selected from a fixed approved catalog rather than accepted as operator input.
/// </summary>
/// <summary>Fixed platform-owned Phase 5B assignment. Parameter and provenance ids are catalog
/// references, never browser or user supplied strategy configuration.</summary>
public sealed record PaperTrainingWorkerSlot(
    int Slot, ExperimentResearchGroup Group, string StrategyId, string Symbol, decimal StartingCash, int Seed,
    string ParameterSetId, string ProvenanceId, CandleInterval Interval = CandleInterval.OneHour,
    string StrategyParameters = "{}", int StrategyVersion = 2,
    PaperRegimeComponentSelection? SelectedComponent = null,
    IReadOnlyList<string>? RankingUniverseSymbols = null,
    DateTimeOffset? AdmissionCloseUtc = null,
    PaperExchangeFilters? PairFilters = null);

public sealed record PaperTrainingStrategyAssignment(int Slot, string StrategyId, string StrategyParameters = "{}");

public sealed record PaperTrainingQualificationGate(
    decimal MinimumNetReturnPercent,
    int MinimumCompletedTrades,
    decimal MaximumDrawdownPercent)
{
    public static PaperTrainingQualificationGate PlatformDefault { get; } = new(0m, 3, 20m);

    public PaperTrainingQualificationGate Validate()
    {
        if (MinimumNetReturnPercent < PlatformDefault.MinimumNetReturnPercent)
            throw new ArgumentOutOfRangeException(nameof(MinimumNetReturnPercent),
                $"Minimum return cannot be below {PlatformDefault.MinimumNetReturnPercent}%.");
        if (MinimumCompletedTrades < PlatformDefault.MinimumCompletedTrades)
            throw new ArgumentOutOfRangeException(nameof(MinimumCompletedTrades),
                $"Minimum completed trades cannot be below {PlatformDefault.MinimumCompletedTrades}.");
        if (MaximumDrawdownPercent is <= 0m or > 100m
            || MaximumDrawdownPercent > PlatformDefault.MaximumDrawdownPercent)
            throw new ArgumentOutOfRangeException(nameof(MaximumDrawdownPercent),
                $"Maximum drawdown must be positive and no greater than {PlatformDefault.MaximumDrawdownPercent}%.");
        return this;
    }
}

public enum PaperTrainingCandidateDisposition
{
    Hold = 0,
    Bearish,
    Vetoed,
    Rejected,
    Queued,
    Admitted
}

public sealed record PaperTrainingScanMetrics(
    int EligiblePairs,
    int EvaluatedCandidates,
    int QualifiedCandidates,
    int AdmittedCandidates,
    int RejectedForDurableEvidence);

public sealed record PaperTrainingQualificationResult(
    int Slot,
    string Symbol,
    bool Accepted,
    decimal NetReturnPercent,
    int CompletedTrades,
    decimal MaximumDrawdownPercent,
    string DatasetFingerprint,
    string Reason,
    string? StrategyId = null,
    bool PaperOnlyExploration = false,
    CandleInterval Interval = CandleInterval.OneHour,
    PaperTrainingCandidateDisposition? CandidateDisposition = null,
    int? BullishChecks = null,
    int? RequiredBullishChecks = null,
    DateTimeOffset? SignalCloseUtc = null,
    PaperTrainingScanMetrics? ScanMetrics = null);

public sealed record PaperTrainingPrerequisites(
    bool DurableClosedCandleSource,
    bool ApprovedResearchGroupsAndGates,
    bool WorkerRiskPolicy,
    bool PaperFillPolicy,
    bool OutputLedger,
    bool ProtectiveScheduler);

public sealed record PaperTrainingActivation(
    Guid OwnerId,
    PaperTrainingActivationState State,
    IReadOnlyList<PaperTrainingWorkerSlot> Slots,
    PaperTrainingPrerequisites Prerequisites,
    DateTimeOffset ChangedAtUtc,
    Guid ChangedBy,
    IReadOnlyList<PaperTrainingQualificationResult>? Qualifications = null,
    IReadOnlyList<PaperTrainingStrategyAssignment>? StrategyAssignments = null,
    IReadOnlyDictionary<string, string>? StrategyParameterSettings = null,
    [property: JsonIgnore] PaperScanUniverseSnapshot? PendingUniverseSnapshot = null)
{
    [JsonIgnore]
    public string? PersistenceRevision { get; init; }

    public bool IsActive => State == PaperTrainingActivationState.Active;
    public IReadOnlyList<PaperTrainingQualificationResult> QualificationResults =>
        Qualifications ?? Array.Empty<PaperTrainingQualificationResult>();
    public IReadOnlyList<PaperTrainingStrategyAssignment> ConfiguredStrategies =>
        StrategyAssignments ?? Array.Empty<PaperTrainingStrategyAssignment>();
    public IReadOnlyDictionary<string, string> ConfiguredStrategyParameters =>
        StrategyParameterSettings ?? ApprovedStrategyParameters.Defaults;
}

/// <summary>
/// A durable implementation must make compare-and-save atomic for state and any persisted
/// revision. Reads are owner-scoped: callers cannot enumerate or address another owner's
/// activation by an unscoped identifier.
/// </summary>
public interface IPaperTrainingActivationRepository
{
    Task<PaperTrainingActivation?> GetAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<bool> TrySaveAsync(PaperTrainingActivation activation, PaperTrainingActivationState? expectedState, CancellationToken cancellationToken = default);
}

public interface IPaperTrainingActivationReader
{
    Task<PaperTrainingActivation?> GetAsync(Guid ownerId, CancellationToken cancellationToken = default);
}

public interface IPaperTrainingActivationSource
{
    Task<IReadOnlyCollection<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken = default);
}

public interface IPaperTrainingProtectionOwnerSource
{
    Task<IReadOnlyCollection<Guid>> GetProtectedOwnerIdsAsync(CancellationToken cancellationToken = default);
}

public sealed record PaperTrainingMarketSubscription(string Symbol, CandleInterval Interval);

public interface IPaperTrainingSubscriptionSource
{
    Task<IReadOnlyCollection<PaperTrainingMarketSubscription>> GetActiveSubscriptionsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Safe host default: no owner is active until a durable activation source is supplied.</summary>
public sealed class DisabledPaperTrainingActivationSource :
    IPaperTrainingActivationSource,
    IPaperTrainingSubscriptionSource,
    IPaperTrainingProtectionOwnerSource
{
    public Task<IReadOnlyCollection<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<Guid>>(Array.Empty<Guid>());
    }

    public Task<IReadOnlyCollection<Guid>> GetProtectedOwnerIdsAsync(CancellationToken cancellationToken = default) =>
        GetActiveOwnerIdsAsync(cancellationToken);

    public Task<IReadOnlyCollection<PaperTrainingMarketSubscription>> GetActiveSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<PaperTrainingMarketSubscription>>(
            Array.Empty<PaperTrainingMarketSubscription>());
    }
}

/// <summary>
/// In-memory test double only. Production composition must supply a durable repository before
/// activation can be enabled; this type is intentionally not registered by the experiment host.
/// </summary>
public sealed class InMemoryPaperTrainingActivationRepository :
    IPaperTrainingActivationRepository,
    IPaperTrainingActivationReader,
    IPaperTrainingActivationSource,
    IPaperTrainingSubscriptionSource
{
    private readonly ConcurrentDictionary<Guid, PaperTrainingActivation> _activations = new();

    public Task<PaperTrainingActivation?> GetAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_activations.TryGetValue(ownerId, out var value) ? value : null);
    }

    public Task<bool> TrySaveAsync(PaperTrainingActivation activation, PaperTrainingActivationState? expectedState, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            if (!_activations.TryGetValue(activation.OwnerId, out var current))
            {
                if (expectedState is not null || !_activations.TryAdd(activation.OwnerId, activation))
                    return Task.FromResult(false);
                return Task.FromResult(true);
            }

            if (expectedState != current.State || !_activations.TryUpdate(activation.OwnerId, activation, current))
                return Task.FromResult(false);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyCollection<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<Guid>>(_activations.Values.Where(value => value.IsActive).Select(value => value.OwnerId).ToArray());

    public Task<IReadOnlyCollection<PaperTrainingMarketSubscription>> GetActiveSubscriptionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyCollection<PaperTrainingMarketSubscription>>(_activations.Values
            .Where(value => value.IsActive)
            .SelectMany(value => value.Slots)
            .SelectMany(slot => ApprovedConsensusStrategyProfiles
                .RequiredIntervals(slot.StrategyId, slot.Interval)
                .Select(
                interval => new PaperTrainingMarketSubscription(slot.Symbol, interval)))
            .Distinct()
            .ToArray());
}

/// <summary>
/// Explicit paper-training state machine. It accepts no account, credential, live-mode, futures,
/// arbitrary balance, symbol, or strategy inputs. An owner, Administrator, or RiskOfficer can
/// start it immediately once every paper-only prerequisite is present.
/// </summary>
public sealed class PaperTrainingActivationService
{
    public const decimal FixedStartingCash = 1_000m;
    public const int MaximumSlots = ExperimentWorker.MaxWorkersPerUser;
    public const string ThreeSwingChannelDivergenceStrategyId = "platform.three-swing-channel-divergence";
    private static readonly PaperTrainingWorkerSlot[] s_catalog =
    [
        new(1, ExperimentResearchGroup.A, "platform.ema-trend-continuation", "XRP/EUR", FixedStartingCash, 104729, "ema-trend-parameters@1", "phase5b-ema-v1"),
        new(2, ExperimentResearchGroup.A, "platform.donchian-breakout-ensemble", "TRX/EUR", FixedStartingCash, 104759, "donchian-breakout-parameters@1", "phase5b-donchian-v1"),
        new(3, ExperimentResearchGroup.A, "platform.bollinger-mean-reversion", "DOGE/EUR", FixedStartingCash, 104761, "bollinger-mean-reversion-parameters@1", "phase5b-bollinger-v1"),
        new(4, ExperimentResearchGroup.A, "platform.rsi-pullback", "ADA/EUR", FixedStartingCash, 104773, "rsi-pullback-parameters@1", "phase5b-rsi-v1"),
        new(5, ExperimentResearchGroup.B, "platform.macd-volume", "XRP/EUR", FixedStartingCash, 104779, "macd-volume-parameters@1", "phase5b-macd-v1"),
        new(6, ExperimentResearchGroup.B, "platform.volatility-compression-breakout", "TRX/EUR", FixedStartingCash, 104789, "volatility-compression-breakout-parameters@1", "phase5b-volatility-v1"),
        new(7, ExperimentResearchGroup.B, "platform.cross-sectional-momentum-rotation", "XBT/EUR", FixedStartingCash, 104801, "cross-sectional-momentum-parameters@1", "phase5b-momentum-v1"),
        new(8, ExperimentResearchGroup.C, "platform.relative-strength-pullback-rotation", "XBT/EUR", FixedStartingCash, 104803, "relative-strength-pullback-parameters@1", "phase5b-relative-strength-v1"),
        new(9, ExperimentResearchGroup.C, "platform.session-conditioned-breakout", "DOGE/EUR", FixedStartingCash, 104827, "session-conditioned-breakout-parameters@1", "phase5b-session-v1"),
        new(10, ExperimentResearchGroup.C, "platform.regime-switching-ensemble", "ADA/EUR", FixedStartingCash, 104831, "regime-switching-ensemble-parameters@1", "phase5b-regime-v1")
    ];
    private static readonly HashSet<string> s_approvedStrategyIds = s_catalog
        .Select(slot => slot.StrategyId)
        .Append(ThreeSwingChannelDivergenceStrategyId)
        .ToHashSet(StringComparer.Ordinal);

    private readonly IPaperTrainingActivationRepository _repository;
    private readonly IAuditEventWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly IPaperWorkerAdmissionLimit _admissionLimit;

    public PaperTrainingActivationService(IPaperTrainingActivationRepository repository, IAuditEventWriter audit, TimeProvider timeProvider)
        : this(repository, audit, timeProvider, new LegacyPaperWorkerAdmissionLimit())
    {
    }

    public PaperTrainingActivationService(IPaperTrainingActivationRepository repository, IAuditEventWriter audit,
        TimeProvider timeProvider, IPaperWorkerAdmissionLimit admissionLimit)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _admissionLimit = admissionLimit ?? throw new ArgumentNullException(nameof(admissionLimit));
    }

    public static IReadOnlyList<PaperTrainingWorkerSlot> ApprovedSlots => s_catalog;
    public static IReadOnlyList<PaperTrainingWorkerSlot> ApprovedStrategyCatalog =>
        s_catalog.Append(CreateApprovedWorkerTemplate(ThreeSwingChannelDivergenceStrategyId)).ToArray();

    public static PaperTrainingWorkerSlot CreateApprovedWorkerTemplate(string strategyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        if (!s_approvedStrategyIds.Contains(strategyId))
            throw new ArgumentException("The requested strategy is not platform-approved.", nameof(strategyId));
        return s_catalog[0] with
        {
            StrategyId = strategyId,
            ParameterSetId = strategyId == ThreeSwingChannelDivergenceStrategyId
                ? "three-swing-channel-divergence-parameters@1"
                : s_catalog[0].ParameterSetId,
            ProvenanceId = strategyId == ThreeSwingChannelDivergenceStrategyId
                ? "platform-three-swing-channel-divergence-v1"
                : s_catalog[0].ProvenanceId
        };
    }

    public async Task<PaperTrainingActivation> StartScannerAsync(
        Guid ownerId,
        Guid actorId,
        RoleType actorRole,
        PaperTrainingPrerequisites prerequisites,
        CancellationToken cancellationToken = default)
    {
        if (actorRole is not (RoleType.User or RoleType.Administrator or RoleType.RiskOfficer))
            throw new UnauthorizedAccessException("Only an owner, Administrator, or RiskOfficer may start paper training.");
        if (actorRole == RoleType.User)
            RequireOwner(ownerId, actorId);
        ValidatePrerequisites(prerequisites);
        await RequireCapacityAsync(ownerId, 1, cancellationToken).ConfigureAwait(false);
        var current = await _repository.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (current?.IsActive == true)
            throw new InvalidOperationException("Paper training is already active for this owner.");
        await RequireCapacityAsync(ownerId, Math.Max(1, current?.Slots.Count ?? 0), cancellationToken)
            .ConfigureAwait(false);
        var assignments = current?.StrategyAssignments is { Count: MaximumSlots } configured
            ? configured
            : s_catalog
                .Select(slot => new PaperTrainingStrategyAssignment(slot.Slot, slot.StrategyId))
                .ToArray();

        var active = new PaperTrainingActivation(
            ownerId,
            PaperTrainingActivationState.Active,
            current?.Slots ?? [],
            prerequisites,
            UtcNow(),
            actorId,
            current?.QualificationResults ?? [],
            assignments,
            current?.ConfiguredStrategyParameters ?? ApprovedStrategyParameters.Defaults)
        { PersistenceRevision = current?.PersistenceRevision };
        if (!await _repository.TrySaveAsync(active, current?.State, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Paper-training start changed concurrently; the scanner was not enabled.");
        await AuditAsync(active, actorId, "PaperTrainingStarted", cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<PaperTrainingActivation> ConfigureScannerStrategiesAsync(
        Guid ownerId,
        Guid actorId,
        RoleType actorRole,
        IReadOnlyCollection<PaperTrainingStrategyAssignment> assignments,
        IReadOnlyDictionary<string, string>? strategyParameterSettings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (actorRole is not (RoleType.User or RoleType.Administrator or RoleType.RiskOfficer))
            throw new UnauthorizedAccessException("Only an owner, Administrator, or RiskOfficer may configure paper workers.");
        if (actorRole == RoleType.User)
            RequireOwner(ownerId, actorId);
        if (assignments.Count != MaximumSlots
            || assignments.Select(assignment => assignment.Slot).Distinct().Count() != MaximumSlots
            || !assignments.Select(assignment => assignment.Slot).Order().SequenceEqual(
                Enumerable.Range(1, MaximumSlots)))
        {
            throw new ArgumentException(
                $"Configure exactly {MaximumSlots} distinct worker slots numbered 1 through {MaximumSlots}.",
                nameof(assignments));
        }

        var normalized = assignments
            .OrderBy(assignment => assignment.Slot)
            .Select(assignment =>
            {
                if (string.IsNullOrWhiteSpace(assignment.StrategyId)
                    || !s_approvedStrategyIds.Contains(assignment.StrategyId))
                {
                    throw new ArgumentException(
                        $"Worker slot {assignment.Slot} must use a platform-approved strategy.",
                        nameof(assignments));
                }
                var strategyId = assignment.StrategyId.Trim();
                if (!ApprovedStrategyParameters.TryNormalize(
                        strategyId,
                        assignment.StrategyParameters,
                        out var strategyParameters,
                        out var parameterError))
                    throw new ArgumentException(
                        $"Worker slot {assignment.Slot}: {parameterError}",
                        nameof(assignments));
                return assignment with { StrategyId = strategyId, StrategyParameters = strategyParameters };
            })
            .ToArray();
        foreach (var strategyGroup in normalized.GroupBy(item => item.StrategyId, StringComparer.Ordinal))
        {
            if (strategyGroup.Select(item => item.StrategyParameters).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new ArgumentException(
                    $"All worker slots assigned to '{strategyGroup.Key}' must use the same strategy settings.",
                    nameof(assignments));
        }

        var current = await _repository.GetAsync(ownerId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Start paper training before configuring its worker slots.");

        var configuredParameters = current.ConfiguredStrategyParameters
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (strategyParameterSettings is not null)
        {
            foreach (var (strategyId, json) in strategyParameterSettings)
            {
                if (!ApprovedStrategyParameters.Catalog.Any(item => item.StrategyId == strategyId))
                    throw new ArgumentException(
                        $"'{strategyId}' is not in the approved strategy catalog.",
                        nameof(strategyParameterSettings));
                if (!ApprovedStrategyParameters.TryNormalize(strategyId, json, out var parameters, out var error))
                    throw new ArgumentException($"{strategyId}: {error}", nameof(strategyParameterSettings));
                configuredParameters[strategyId] = parameters;
            }
        }

        foreach (var assignment in normalized)
        {
            if (strategyParameterSettings is not null
                && !string.Equals(
                    configuredParameters[assignment.StrategyId],
                    assignment.StrategyParameters,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Worker slot {assignment.Slot} settings must match the saved settings for '{assignment.StrategyId}'.",
                    nameof(assignments));
            }
            configuredParameters[assignment.StrategyId] = assignment.StrategyParameters;
        }

        var configuredAtUtc = UtcNow();
        var updated = current with
        {
            StrategyAssignments = normalized,
            StrategyParameterSettings = configuredParameters,
            ChangedAtUtc = current.IsActive ? current.ChangedAtUtc : configuredAtUtc,
            ChangedBy = current.IsActive ? current.ChangedBy : actorId
        };
        if (!await _repository.TrySaveAsync(updated, current.State, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Paper-worker configuration changed concurrently; no changes were applied.");
        await AuditAsync(updated, actorId, "PaperTrainingWorkerStrategiesConfigured", cancellationToken, configuredAtUtc)
            .ConfigureAwait(false);
        return updated;
    }

    public async Task<PaperTrainingActivation> StartAsync(
        Guid ownerId, Guid actorId, RoleType actorRole, int requestedSlots, PaperTrainingPrerequisites prerequisites,
        CancellationToken cancellationToken = default)
    {
        if (actorRole is not (RoleType.User or RoleType.Administrator or RoleType.RiskOfficer))
            throw new UnauthorizedAccessException("Only an owner, Administrator, or RiskOfficer may start paper training.");
        if (actorRole == RoleType.User)
            RequireOwner(ownerId, actorId);
        ValidateSlots(requestedSlots);
        ValidatePrerequisites(prerequisites);
        await RequireCapacityAsync(ownerId, requestedSlots, cancellationToken).ConfigureAwait(false);
        var current = await _repository.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (current?.IsActive == true)
            throw new InvalidOperationException("Paper training is already active for this owner.");

        var active = new PaperTrainingActivation(ownerId, PaperTrainingActivationState.Active,
            s_catalog.Take(requestedSlots).ToArray(), prerequisites, UtcNow(), actorId,
            StrategyParameterSettings: current?.ConfiguredStrategyParameters ?? ApprovedStrategyParameters.Defaults)
        { PersistenceRevision = current?.PersistenceRevision };
        if (!await _repository.TrySaveAsync(active, current?.State, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Paper-training start changed concurrently; no workers were started.");
        await AuditAsync(active, actorId, "PaperTrainingStarted", cancellationToken).ConfigureAwait(false);
        return active;
    }

    public async Task<PaperTrainingActivation> StartQualifiedAsync(
        Guid ownerId,
        Guid actorId,
        RoleType actorRole,
        IReadOnlyList<PaperTrainingWorkerSlot> requestedSlots,
        IReadOnlyList<PaperTrainingQualificationResult> qualifications,
        PaperTrainingPrerequisites prerequisites,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedSlots);
        ArgumentNullException.ThrowIfNull(qualifications);
        if (actorRole is not (RoleType.User or RoleType.Administrator or RoleType.RiskOfficer))
            throw new UnauthorizedAccessException("Only an owner, Administrator, or RiskOfficer may start paper training.");
        if (actorRole == RoleType.User)
            RequireOwner(ownerId, actorId);
        ValidatePrerequisites(prerequisites);
        if (requestedSlots.Count is < 1 or > MaximumSlots
            || requestedSlots.Select(slot => slot.Slot).Distinct().Count() != requestedSlots.Count)
            throw new ArgumentException($"Select between one and {MaximumSlots} distinct approved slots.", nameof(requestedSlots));
        if (requestedSlots.Any(slot => !IsApprovedTemplate(slot)))
            throw new ArgumentException("Every paper-training slot must come from the approved platform catalog.", nameof(requestedSlots));
        if (requestedSlots.Any(slot => !PaperTrainingAutoSelectionService.ApprovedIntervals.Contains(slot.Interval)))
            throw new ArgumentException("Every paper-training slot must use an approved paper interval.", nameof(requestedSlots));
        if (requestedSlots.Any(slot => slot.StartingCash <= 0m))
            throw new ArgumentOutOfRangeException(nameof(requestedSlots), "Every fake starting balance must be positive.");
        await RequireCapacityAsync(ownerId, requestedSlots.Count, cancellationToken).ConfigureAwait(false);
        var requestedSlotIds = requestedSlots.Select(slot => slot.Slot).ToHashSet();
        var qualificationSlotIds = qualifications.Select(result => result.Slot).ToHashSet();
        if (qualifications.Count != requestedSlots.Count
            || qualificationSlotIds.Count != qualifications.Count
            || !qualificationSlotIds.SetEquals(requestedSlotIds))
            throw new ArgumentException("Every requested slot requires one qualification result.", nameof(qualifications));
        var slotsById = requestedSlots.ToDictionary(slot => slot.Slot);
        if (qualifications.Any(result =>
                !string.Equals(result.Symbol, slotsById[result.Slot].Symbol, StringComparison.OrdinalIgnoreCase)
                || result.Interval != slotsById[result.Slot].Interval
                || (result.StrategyId is not null
                    && !string.Equals(result.StrategyId, slotsById[result.Slot].StrategyId, StringComparison.Ordinal))))
            throw new ArgumentException(
                "Qualification evidence must match the exact slot strategy, symbol, and interval.",
                nameof(qualifications));

        var current = await _repository.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (current?.IsActive == true)
            throw new InvalidOperationException("Paper training is already active for this owner.");

        var runnableSlotIds = qualifications
            .Where(result => result.Accepted || result.PaperOnlyExploration)
            .Select(result => result.Slot)
            .ToHashSet();
        var runnableSlots = requestedSlots.Where(slot => runnableSlotIds.Contains(slot.Slot)).ToArray();
        var state = runnableSlots.Length == 0
            ? PaperTrainingActivationState.Disabled
            : PaperTrainingActivationState.Active;
        var activation = new PaperTrainingActivation(
            ownerId, state, runnableSlots, prerequisites, UtcNow(), actorId, qualifications.ToArray(),
            StrategyParameterSettings: current?.ConfiguredStrategyParameters ?? ApprovedStrategyParameters.Defaults)
        { PersistenceRevision = current?.PersistenceRevision };
        if (!await _repository.TrySaveAsync(activation, current?.State, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Paper-training start changed concurrently; no workers were started.");
        var auditAction = state != PaperTrainingActivationState.Active
            ? "PaperTrainingQualificationRejected"
            : qualifications.Any(result => result.PaperOnlyExploration)
                ? "PaperTrainingExplorationStarted"
                : "PaperTrainingQualifiedAndStarted";
        await AuditAsync(activation, actorId, auditAction, cancellationToken).ConfigureAwait(false);
        return activation;
    }

    public Task<PaperTrainingActivation> DisableAsync(Guid ownerId, Guid actorId, RoleType actorRole, CancellationToken cancellationToken = default) =>
        StopAsync(ownerId, actorId, actorRole, PaperTrainingActivationState.Disabled, "PaperTrainingDisabled", cancellationToken);

    public Task<PaperTrainingActivation> EmergencyStopAsync(Guid ownerId, Guid actorId, RoleType actorRole, CancellationToken cancellationToken = default) =>
        StopAsync(ownerId, actorId, actorRole, PaperTrainingActivationState.EmergencyStopped, "PaperTrainingEmergencyStopped", cancellationToken);

    private async Task RequireCapacityAsync(Guid ownerId, int requestedSlots, CancellationToken cancellationToken)
    {
        var maximum = await _admissionLimit.GetMaximumAsync(ownerId, UtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (maximum is < 0 or > MaximumSlots)
            throw new InvalidOperationException("The paper entitlement has an invalid worker ceiling.");
        if (requestedSlots > maximum)
            throw new InvalidOperationException("An active paper entitlement is required for the requested worker count.");
    }

    private async Task<PaperTrainingActivation> StopAsync(Guid ownerId, Guid actorId, RoleType actorRole, PaperTrainingActivationState state, string action, CancellationToken cancellationToken)
    {
        if (actorRole is not (RoleType.User or RoleType.Administrator or RoleType.RiskOfficer))
            throw new UnauthorizedAccessException("Only the owner, Administrator, or RiskOfficer may stop paper training.");
        if (actorRole == RoleType.User)
            RequireOwner(ownerId, actorId);
        var current = await _repository.GetAsync(ownerId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Paper training was not started.");
        var stopped = current with { State = state, ChangedAtUtc = UtcNow(), ChangedBy = actorId };
        if (!await _repository.TrySaveAsync(stopped, current.State, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Paper-training state changed concurrently; no transition occurred.");
        await AuditAsync(stopped, actorId, action, cancellationToken).ConfigureAwait(false);
        return stopped;
    }

    private async Task AuditAsync(
        PaperTrainingActivation activation, Guid actorId, string action,
        CancellationToken cancellationToken, DateTimeOffset? occurredAtUtc = null) =>
        await _audit.WriteAsync(new AuditEvent(Guid.NewGuid(), actorId, action, "PaperTrainingActivation",
            activation.OwnerId.ToString("N"), occurredAtUtc ?? activation.ChangedAtUtc, null,
            $"state={activation.State};slots={activation.Slots.Count};paperOnly=true", null), cancellationToken).ConfigureAwait(false);

    private static void ValidatePrerequisites(PaperTrainingPrerequisites prerequisites)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        if (!prerequisites.DurableClosedCandleSource || !prerequisites.ApprovedResearchGroupsAndGates
            || !prerequisites.WorkerRiskPolicy || !prerequisites.PaperFillPolicy || !prerequisites.OutputLedger
            || !prerequisites.ProtectiveScheduler)
            throw new InvalidOperationException("Paper-training activation requires every paper-only prerequisite.");
    }

    private static void ValidateSlots(int requestedSlots)
    {
        if (requestedSlots is < 1 or > MaximumSlots)
            throw new ArgumentOutOfRangeException(nameof(requestedSlots), $"Paper training supports one to {MaximumSlots} fixed worker slots.");
    }

    private static bool IsApprovedTemplate(PaperTrainingWorkerSlot slot) =>
        !string.IsNullOrWhiteSpace(slot.Symbol)
        && s_catalog.Any(approved =>
            approved.Group == slot.Group
            && approved.StrategyId.Equals(slot.StrategyId, StringComparison.Ordinal)
            && approved.ParameterSetId.Equals(slot.ParameterSetId, StringComparison.Ordinal)
            && approved.ProvenanceId.Equals(slot.ProvenanceId, StringComparison.Ordinal));

    private static void RequireOwner(Guid ownerId, Guid actorId)
    {
        if (ownerId == Guid.Empty || actorId == Guid.Empty || ownerId != actorId)
            throw new UnauthorizedAccessException("A user may start or disable only their own paper training.");
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}
