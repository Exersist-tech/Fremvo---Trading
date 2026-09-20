using System.Collections.Concurrent;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;

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
    string ParameterSetId, string ProvenanceId);

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
    Guid ChangedBy)
{
    public bool IsActive => State == PaperTrainingActivationState.Active;
}

/// <summary>
/// A durable implementation must make compare-and-save atomic. Reads are owner-scoped: callers
/// cannot enumerate or address another owner's activation by an unscoped identifier.
/// </summary>
public interface IPaperTrainingActivationRepository
{
    Task<PaperTrainingActivation?> GetAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<bool> TrySaveAsync(PaperTrainingActivation activation, PaperTrainingActivationState? expectedState, CancellationToken cancellationToken = default);
}

public interface IPaperTrainingActivationSource
{
    Task<IReadOnlyCollection<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Safe host default: no owner is active until a durable activation source is supplied.</summary>
public sealed class DisabledPaperTrainingActivationSource : IPaperTrainingActivationSource
{
    public Task<IReadOnlyCollection<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyCollection<Guid>>(Array.Empty<Guid>());
    }
}

/// <summary>
/// In-memory test double only. Production composition must supply a durable repository before
/// activation can be enabled; this type is intentionally not registered by the experiment host.
/// </summary>
public sealed class InMemoryPaperTrainingActivationRepository : IPaperTrainingActivationRepository, IPaperTrainingActivationSource
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
    private static readonly PaperTrainingWorkerSlot[] s_catalog =
    [
        new(1, ExperimentResearchGroup.A, "platform.ema-trend-continuation", "BTC/USD", FixedStartingCash, 104729, "ema-trend-parameters@1", "phase5b-ema-v1"),
        new(2, ExperimentResearchGroup.A, "platform.donchian-breakout-ensemble", "BTC/USD", FixedStartingCash, 104759, "donchian-breakout-parameters@1", "phase5b-donchian-v1"),
        new(3, ExperimentResearchGroup.A, "platform.bollinger-mean-reversion", "BTC/USD", FixedStartingCash, 104761, "bollinger-mean-reversion-parameters@1", "phase5b-bollinger-v1"),
        new(4, ExperimentResearchGroup.A, "platform.rsi-pullback", "BTC/USD", FixedStartingCash, 104773, "rsi-pullback-parameters@1", "phase5b-rsi-v1"),
        new(5, ExperimentResearchGroup.B, "platform.macd-volume", "BTC/USD", FixedStartingCash, 104779, "macd-volume-parameters@1", "phase5b-macd-v1"),
        new(6, ExperimentResearchGroup.B, "platform.volatility-compression-breakout", "BTC/USD", FixedStartingCash, 104789, "volatility-compression-breakout-parameters@1", "phase5b-volatility-v1"),
        new(7, ExperimentResearchGroup.B, "platform.cross-sectional-momentum-rotation", "BTC/USD", FixedStartingCash, 104801, "cross-sectional-momentum-parameters@1", "phase5b-momentum-v1"),
        new(8, ExperimentResearchGroup.C, "platform.relative-strength-pullback-rotation", "BTC/USD", FixedStartingCash, 104803, "relative-strength-pullback-parameters@1", "phase5b-relative-strength-v1"),
        new(9, ExperimentResearchGroup.C, "platform.session-conditioned-breakout", "BTC/USD", FixedStartingCash, 104827, "session-conditioned-breakout-parameters@1", "phase5b-session-v1"),
        new(10, ExperimentResearchGroup.C, "platform.regime-switching-ensemble", "BTC/USD", FixedStartingCash, 104831, "regime-switching-ensemble-parameters@1", "phase5b-regime-v1")
    ];

    private readonly IPaperTrainingActivationRepository _repository;
    private readonly IAuditEventWriter _audit;
    private readonly TimeProvider _timeProvider;

    public PaperTrainingActivationService(IPaperTrainingActivationRepository repository, IAuditEventWriter audit, TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public static IReadOnlyList<PaperTrainingWorkerSlot> ApprovedSlots => s_catalog;

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
        var current = await _repository.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (current?.IsActive == true)
            throw new InvalidOperationException("Paper training is already active for this owner.");

        var active = new PaperTrainingActivation(ownerId, PaperTrainingActivationState.Active,
            s_catalog.Take(requestedSlots).ToArray(), prerequisites, UtcNow(), actorId);
        if (!await _repository.TrySaveAsync(active, current?.State, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Paper-training start changed concurrently; no workers were started.");
        await AuditAsync(active, actorId, "PaperTrainingStarted", cancellationToken).ConfigureAwait(false);
        return active;
    }

    public Task<PaperTrainingActivation> DisableAsync(Guid ownerId, Guid actorId, RoleType actorRole, CancellationToken cancellationToken = default) =>
        StopAsync(ownerId, actorId, actorRole, PaperTrainingActivationState.Disabled, "PaperTrainingDisabled", cancellationToken);

    public Task<PaperTrainingActivation> EmergencyStopAsync(Guid ownerId, Guid actorId, RoleType actorRole, CancellationToken cancellationToken = default) =>
        StopAsync(ownerId, actorId, actorRole, PaperTrainingActivationState.EmergencyStopped, "PaperTrainingEmergencyStopped", cancellationToken);

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

    private async Task AuditAsync(PaperTrainingActivation activation, Guid actorId, string action, CancellationToken cancellationToken) =>
        await _audit.WriteAsync(new AuditEvent(Guid.NewGuid(), actorId, action, "PaperTrainingActivation",
            activation.OwnerId.ToString("N"), activation.ChangedAtUtc, null,
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

    private static void RequireOwner(Guid ownerId, Guid actorId)
    {
        if (ownerId == Guid.Empty || actorId == Guid.Empty || ownerId != actorId)
            throw new UnauthorizedAccessException("A user may start or disable only their own paper training.");
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}
