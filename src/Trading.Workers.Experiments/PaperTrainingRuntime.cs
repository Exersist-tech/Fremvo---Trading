using System.Security.Cryptography;
using System.Text;
using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Strategies;
using Trading.Strategies.Approvals;
using Trading.Infrastructure.Data.Experiments;
using Trading.Backtesting;

namespace Trading.Workers.Experiments;

public sealed class ScopedDurableCandleSource : IExperimentCandleSeriesSource
{
    private readonly IServiceScopeFactory _scopes;
    public ScopedDurableCandleSource(IServiceScopeFactory scopes) => _scopes = scopes;
    public async Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(ExperimentCandleSeriesRequest request, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        return await new DurableExperimentCandleSeriesSource(scope.ServiceProvider.GetRequiredService<ICandleRepository>())
            .GetClosedSeriesAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ScopedDecisionLedger : IExperimentDecisionLedger
{
    private readonly IServiceScopeFactory _scopes;
    public ScopedDecisionLedger(IServiceScopeFactory scopes) => _scopes = scopes;
    public async Task<(ExperimentDecisionWriteResult Result, ExperimentDecisionRecord? Record)> RecordAsync(Guid userId, ExperimentDecisionRecord record, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EfExperimentDecisionLedger>().RecordAsync(userId, record, cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<ExperimentDecisionRecord>> ListAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EfExperimentDecisionLedger>().ListAsync(userId, workerId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Uses a fresh EF scope per worker operation while preserving strict owner scoping.</summary>
public sealed class ScopedExperimentWorkerRepository : IExperimentWorkerRepository, IPaperTradingLedgerRepository
    {
        private readonly IServiceScopeFactory _scopes;
        public ScopedExperimentWorkerRepository(IServiceScopeFactory scopes) => _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        public async Task<ExperimentWorker?> GetAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EfExperimentWorkerRepository>().GetAsync(userId, workerId, cancellationToken).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<ExperimentWorker>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EfExperimentWorkerRepository>().ListAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<ExperimentWorker>> ListByNamesAsync(
            Guid userId,
            IReadOnlyCollection<string> names,
            CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EfExperimentWorkerRepository>()
                .ListByNamesAsync(userId, names, cancellationToken)
                .ConfigureAwait(false);
        }
        public async Task<int> CountAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EfExperimentWorkerRepository>().CountAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        public async Task SaveAsync(ExperimentWorker worker, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<EfExperimentWorkerRepository>().SaveAsync(worker, cancellationToken).ConfigureAwait(false);
        }
        public async Task AddAsync(Guid userId, PaperTradingLedgerEntry entry, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<EfExperimentWorkerRepository>().AddAsync(userId, entry, cancellationToken).ConfigureAwait(false);
        }
        public async Task<IReadOnlyCollection<PaperTradingLedgerEntry>> ListAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EfExperimentWorkerRepository>().ListAsync(userId, workerId, cancellationToken).ConfigureAwait(false);
        }
}

public sealed class ScopedPaperExecutionLedger : IExperimentPaperExecutionLedger
    {
        private readonly IServiceScopeFactory _scopes;
        public ScopedPaperExecutionLedger(IServiceScopeFactory scopes) => _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        public async Task<(ExperimentPaperExecutionClaimResult Result, ExperimentPaperExecutionAssociation? Association)> ClaimAsync(
            Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EfExperimentPaperExecutionLedger>().ClaimAsync(userId, association, cancellationToken).ConfigureAwait(false);
        }
        public async Task CompleteAsync(Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<EfExperimentPaperExecutionLedger>().CompleteAsync(userId, association, cancellationToken).ConfigureAwait(false);
        }
}

public sealed class ScopedPaperPlanEvidenceRepository : IExperimentPaperPlanEvidenceRepository
{
    private readonly IServiceScopeFactory _scopes;
    public ScopedPaperPlanEvidenceRepository(IServiceScopeFactory scopes) => _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    public async Task SaveAsync(ExperimentPaperPlanEvidence evidence, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EfExperimentPaperPlanEvidenceRepository>().SaveAsync(evidence, cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<ExperimentPaperPlanEvidence>> ListAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EfExperimentPaperPlanEvidenceRepository>().ListAsync(userId, workerId, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Builds exits only from a replayed open paper position and its immutable approved plan.</summary>
public sealed class DurablePaperTrainingProtectiveExitPositionSource : IExperimentProtectiveExitPositionSource
{
    private readonly IExperimentWorkerRepository _workers;
    private readonly IExperimentPaperPlanEvidenceRepository _plans;

    public DurablePaperTrainingProtectiveExitPositionSource(
        IExperimentWorkerRepository workers,
        IExperimentPaperPlanEvidenceRepository plans) => (_workers, _plans) = (
            workers ?? throw new ArgumentNullException(nameof(workers)),
            plans ?? throw new ArgumentNullException(nameof(plans)));

    public async Task<IReadOnlyList<ExperimentProtectiveExitPosition>> ListOpenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            return [];

        var positions = new List<ExperimentProtectiveExitPosition>();
        foreach (var worker in await _workers.ListAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (worker.UserId != userId || worker.Status != ExperimentWorkerStatus.Running || worker.PositionQuantity <= 0m)
                continue;

            var openedAt = CurrentPositionOpenedAt(worker);
            if (openedAt is null)
                continue;
            var plan = (await _plans.ListAsync(userId, worker.Id, cancellationToken).ConfigureAwait(false))
                .Where(candidate => candidate.DecisionKey.Symbol.Equals(worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
                    && candidate.DecisionKey.AsOfUtc <= openedAt.Value
                    && candidate.ProtectiveStopPrice > 0m)
                .OrderByDescending(candidate => candidate.DecisionKey.AsOfUtc)
                .ThenByDescending(candidate => candidate.RecordedAtUtc)
                .FirstOrDefault();
            if (plan is null)
                continue;

            positions.Add(new ExperimentProtectiveExitPosition(userId, worker.Id, PositionId(plan.DecisionKey),
                worker.MarketSymbol, worker.PositionQuantity, worker.AverageEntryPrice, openedAt.Value,
                plan.ProtectiveStopPrice, plan.ConservativeTargetPrice, worker));
        }
        return positions;
    }

    private static DateTimeOffset? CurrentPositionOpenedAt(ExperimentWorker worker)
    {
        var quantity = 0m;
        DateTimeOffset? opened = null;
        foreach (var entry in worker.Ledger.OrderBy(value => value.OccurredAtUtc).ThenBy(value => value.Id))
        {
            if (entry.Direction.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                if (quantity == 0m)
                    opened = entry.OccurredAtUtc;
                quantity += entry.Quantity;
            }
            else if (entry.Direction.Equals("sell", StringComparison.OrdinalIgnoreCase))
            {
                quantity -= entry.Quantity;
                if (quantity == 0m)
                    opened = null;
            }
        }
        return quantity == worker.PositionQuantity && quantity > 0m ? opened : null;
    }

    private static Guid PositionId(ExperimentDecisionKey key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{key.UserId:D}|{key.WorkerId:D}|{key.StrategyFingerprint}|{key.OpenTimeUtc:O}|{key.CloseTimeUtc:O}|{key.AsOfUtc:O}"));
        return new Guid(bytes[..16]);
    }
}

/// <summary>Maps a protective-exit identity onto the durable paper execution claim store.</summary>
public sealed class DurablePaperTrainingProtectiveExitLedger : IExperimentProtectiveExitLedger
{
    private const string ClaimStrategy = "experiment-protective-exit-claim";
    private readonly IExperimentPaperExecutionLedger _ledger;

    public DurablePaperTrainingProtectiveExitLedger(IExperimentPaperExecutionLedger ledger) =>
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    public async Task<ExperimentProtectiveExitClaimResult> ClaimAsync(ExperimentProtectiveExitKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var association = Association(key, ExperimentPaperExecutionStatus.Claimed, null);
        var result = await _ledger.ClaimAsync(key.UserId, association, cancellationToken).ConfigureAwait(false);
        return result.Result switch
        {
            ExperimentPaperExecutionClaimResult.Claimed => ExperimentProtectiveExitClaimResult.Claimed,
            ExperimentPaperExecutionClaimResult.Existing => ExperimentProtectiveExitClaimResult.Existing,
            _ => ExperimentProtectiveExitClaimResult.Conflict
        };
    }

    public Task CompleteAsync(ExperimentProtectiveExitKey key, ExperimentPaperExecutionStatus status, string detail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _ledger.CompleteAsync(key.UserId, Association(key, status, detail), cancellationToken);
    }

    private static ExperimentPaperExecutionAssociation Association(
        ExperimentProtectiveExitKey key, ExperimentPaperExecutionStatus status, string? detail)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{key.PositionId:D}|{key.ProtectiveExitIdentity}")));
        var decision = new ExperimentDecisionKey(key.UserId, key.WorkerId, 0, ExperimentResearchGroup.A,
            ClaimStrategy, 1, fingerprint, "protective-exit", CandleInterval.OneMinute,
            key.CandleCloseTimeUtc.AddMinutes(-1), key.CandleCloseTimeUtc, key.CandleCloseTimeUtc);
        return new(decision, $"paper-protective-exit-{fingerprint}", status, null, detail);
    }
}

/// <summary>
/// Creates the fixed catalog workers and immutable approved-paper configuration only after the
/// durable activation source has selected an owner. No browser input is used here.
/// </summary>
public sealed class PaperTrainingConfigurationSource : IExperimentResearchGroupConfigurationSource
{
    private static readonly Guid s_instrument = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const int MinimumClosedHistoryCandles = 35;
    private readonly IExperimentWorkerRepository _workers;
    private readonly TimeProvider _time;
    private readonly ApprovedExperimentStrategyRegistry _registry;
    private readonly IPaperTrainingActivationReader _activations;

    public PaperTrainingConfigurationSource(
        IExperimentWorkerRepository workers,
        TimeProvider time,
        ApprovedExperimentStrategyRegistry registry,
        IPaperTrainingActivationReader activations)
    {
        _workers = workers;
        _time = time;
        _registry = registry;
        _activations = activations;
    }

    public async Task<ExperimentResearchGroupConfiguration?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var activation = await _activations.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (activation is not { IsActive: true } || activation.Slots.Count == 0)
            return null;

        var workers = await _workers.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        var sessionSuffix = activation.ChangedAtUtc.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var slot in activation.Slots)
        {
            var name = $"Paper training {slot.Slot} {sessionSuffix}";
            if (workers.Any(worker => worker.Name.Equals(name, StringComparison.Ordinal)
                && worker.StrategyId.Equals(slot.StrategyId, StringComparison.Ordinal)
                && worker.MarketSymbol.Equals(slot.Symbol, StringComparison.Ordinal)))
                continue;
            var createdWorker = new ExperimentWorker(
                Guid.NewGuid(), userId, name, slot.StrategyId, slot.Symbol,
                slot.StartingCash, _time.GetUtcNow(), slot.Seed);
            createdWorker.Start();
            await _workers.SaveAsync(createdWorker, cancellationToken).ConfigureAwait(false);
        }
        workers = await _workers.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        var selectedNames = activation.Slots
            .Select(slot => $"Paper training {slot.Slot} {sessionSuffix}")
            .ToHashSet(StringComparer.Ordinal);
        workers = workers.Where(worker => selectedNames.Contains(worker.Name)).ToArray();

        var now = _time.GetUtcNow();
        var slotsByName = activation.Slots.ToDictionary(
            slot => $"Paper training {slot.Slot} {sessionSuffix}",
            StringComparer.Ordinal);
        var provenances = workers.ToDictionary(
            worker => worker.Id,
            worker => CreateProvenance(worker, slotsByName[worker.Name].Interval, now));
        return ExperimentResearchGroupConfiguration.Create(userId, 1, workers, provenances);
    }

    private ExperimentResearchProvenance CreateProvenance(
        ExperimentWorker worker,
        CandleInterval interval,
        DateTimeOffset now)
    {
        var definition = _registry.Definitions.Single(x => x.FamilyId == worker.StrategyId);
        var intervalDuration = TimeSpan.FromMinutes((int)interval);
        var approval = StrategyApproval.CreateDraft(Guid.NewGuid(),
            new StrategyVersion(new StrategyTemplateVersionIdentity(definition.FamilyId, definition.Version),
                new StrategyParameterSchemaReference(definition.ParameterSchemaId, definition.ParameterSchemaVersion, definition.ParameterSchemaFingerprint),
                definition.ContentFingerprint, now), StrategyApprovalActor.Human(Guid.NewGuid()), now,
            new StrategyApprovalRequirements(new[] { new ApprovedInstrumentScope(AssetClass.Cryptocurrency, s_instrument) },
                MinimumClosedHistoryCandles, 1m, 1m, 1m, TimeSpan.FromHours(2), PaperTrainingAutoSelectionService.ApprovedIntervals,
                new[] { TradingProductType.Spot }, new[] { StrategyApprovalMode.Paper },
                new StrategyTimeframeConfiguration(CandleInterval.OneHour, interval, interval)));
        approval = approval.TransitionTo(StrategyApprovalState.UnderReview, approval.CreatedBy, now)
            .TransitionTo(StrategyApprovalState.Approved, approval.CreatedBy, now, approval.CreatedBy);
        var utcTicks = now.ToUniversalTime().Ticks;
        var completedIntervalUtc = new DateTimeOffset(
            utcTicks - (utcTicks % intervalDuration.Ticks),
            TimeSpan.Zero);
        var dataset = new HistoricalDataset($"paper-training-candles-{worker.Id:N}", "durable-candle-repository", worker.MarketSymbol,
            IntervalCode(interval), completedIntervalUtc.AddTicks(-intervalDuration.Ticks * MinimumClosedHistoryCandles),
            completedIntervalUtc, MinimumClosedHistoryCandles, Fingerprint, "catalog-v1", now);
        var evidence = new StrategyResearchEvidence(new ResearchEvidenceProvenance("durable-candle-repository", Fingerprint, now),
            new StrategyApprovalEvidence(s_instrument, AssetClass.Cryptocurrency, MinimumClosedHistoryCandles, 10m, .1m, .01m, now),
            true, 1m, 1m, true, 1m, 1m, 1m, Fingerprint);
        var gates = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(
            new StrategyRejectionGateEvaluationInput(approval, approval.Requirements?.TimeframeConfiguration, TradingProductType.Spot, StrategyApprovalMode.Paper, evidence, now));
        return new ExperimentResearchProvenance(approval,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worker.StrategyParameters.Trim()))),
            dataset, new ExperimentClassifierReference("platform-regime", 1, Fingerprint), evidence.Provenance, gates);
    }

    private static string IntervalCode(CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMinutes => "5M",
        CandleInterval.FifteenMinutes => "15M",
        CandleInterval.ThirtyMinutes => "30M",
        CandleInterval.OneHour => "1H",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), "Paper-training interval is not approved.")
    };
}

/// <summary>Runs durable-candle analysis and records the attested neutral/blocked decision.</summary>
public sealed class PaperTrainingObservationRunner : IExperimentWorkerRunner
{
    private readonly PaperExperimentWorkerRunner _analysis;
    private readonly IExperimentResearchGroupConfigurationSource _configurations;
    private readonly ExperimentDecisionPolicy _decisions;
    private readonly TimeProvider _time;
    public PaperTrainingObservationRunner(PaperExperimentWorkerRunner analysis, IExperimentResearchGroupConfigurationSource configurations,
        ExperimentDecisionPolicy decisions, TimeProvider time) => (_analysis, _configurations, _decisions, _time) = (analysis, configurations, decisions, time);
    public async Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var now = _time.GetUtcNow();
        var configuration = await _configurations.GetAsync(worker.UserId, cancellationToken).ConfigureAwait(false);
        var assignment = configuration?.Assignments.SingleOrDefault(x => x.WorkerId == worker.Id);
        if (configuration is null || assignment is null) return;
        var observation = await _analysis
            .AnalyzeAcrossPaperTimeframesAsync(worker, configuration, assignment, now, cancellationToken)
            .ConfigureAwait(false);
        if (observation.Evidence is null) return;
        await _decisions.DecideAsync(worker, configuration, assignment, observation,
            new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, worker.PositionQuantity, now),
            new ExperimentClosedCandleIdentity(observation.Evidence.Symbol, observation.Evidence.Interval, observation.Evidence.OpenTimeUtc, observation.Evidence.CloseTimeUtc, now), cancellationToken).ConfigureAwait(false);
    }
}
