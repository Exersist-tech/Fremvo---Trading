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

/// <summary>
/// Creates the fixed catalog workers and immutable approved-paper configuration only after the
/// durable activation source has selected an owner. No browser input is used here.
/// </summary>
public sealed class PaperTrainingConfigurationSource : IExperimentResearchGroupConfigurationSource
{
    private static readonly Guid s_instrument = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private readonly IExperimentWorkerRepository _workers;
    private readonly TimeProvider _time;
    private readonly ApprovedExperimentStrategyRegistry _registry;

    public PaperTrainingConfigurationSource(IExperimentWorkerRepository workers, TimeProvider time, ApprovedExperimentStrategyRegistry registry)
    {
        _workers = workers; _time = time; _registry = registry;
    }

    public async Task<ExperimentResearchGroupConfiguration?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var workers = await _workers.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        if (workers.Count == 0)
        {
            foreach (var slot in PaperTrainingActivationService.ApprovedSlots)
            {
                var createdWorker = new ExperimentWorker(Guid.NewGuid(), userId, $"Paper training {slot.Slot}", slot.StrategyId,
                    slot.Symbol, slot.StartingCash, _time.GetUtcNow(), slot.Seed);
                createdWorker.Start();
                await _workers.SaveAsync(createdWorker, cancellationToken).ConfigureAwait(false);
            }
            workers = await _workers.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        var provenances = workers.ToDictionary(worker => worker.Id, worker => CreateProvenance(worker, now));
        return ExperimentResearchGroupConfiguration.Create(userId, 1, workers, provenances);
    }

    private ExperimentResearchProvenance CreateProvenance(ExperimentWorker worker, DateTimeOffset now)
    {
        var definition = _registry.Definitions.Single(x => x.FamilyId == worker.StrategyId);
        var approval = StrategyApproval.CreateDraft(Guid.NewGuid(),
            new StrategyVersion(new StrategyTemplateVersionIdentity(definition.FamilyId, definition.Version),
                new StrategyParameterSchemaReference(definition.ParameterSchemaId, definition.ParameterSchemaVersion, definition.ParameterSchemaFingerprint),
                definition.ContentFingerprint, now), StrategyApprovalActor.Human(Guid.NewGuid()), now,
            new StrategyApprovalRequirements(new[] { new ApprovedInstrumentScope(AssetClass.Cryptocurrency, s_instrument) },
                3, 1m, 1m, 1m, TimeSpan.FromHours(1), new[] { CandleInterval.OneHour },
                new[] { TradingProductType.Spot }, new[] { StrategyApprovalMode.Paper },
                new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.OneHour, CandleInterval.OneHour)));
        approval = approval.TransitionTo(StrategyApprovalState.UnderReview, approval.CreatedBy, now)
            .TransitionTo(StrategyApprovalState.Approved, approval.CreatedBy, now, approval.CreatedBy);
        var dataset = new HistoricalDataset($"paper-training-candles-{worker.Id:N}", "durable-candle-repository", worker.MarketSymbol,
            "1H", now.AddDays(-2), now, 3, Fingerprint, "catalog-v1", now);
        var evidence = new StrategyResearchEvidence(new ResearchEvidenceProvenance("durable-candle-repository", Fingerprint, now),
            new StrategyApprovalEvidence(s_instrument, AssetClass.Cryptocurrency, 3, 10m, .1m, .01m, now),
            true, 1m, 1m, true, 1m, 1m, 1m, Fingerprint);
        var gates = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(
            new StrategyRejectionGateEvaluationInput(approval, approval.Requirements?.TimeframeConfiguration, TradingProductType.Spot, StrategyApprovalMode.Paper, evidence, now));
        return new ExperimentResearchProvenance(approval,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worker.StrategyParameters.Trim()))),
            dataset, new ExperimentClassifierReference("platform-regime", 1, Fingerprint), evidence.Provenance, gates);
    }
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
        var observation = await _analysis.AnalyzeAsync(worker, configuration, assignment, now, cancellationToken).ConfigureAwait(false);
        if (observation.Evidence is null) return;
        await _decisions.DecideAsync(worker, configuration, assignment, observation,
            new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, worker.PositionQuantity, now),
            new ExperimentClosedCandleIdentity(observation.Evidence.Symbol, observation.Evidence.Interval, observation.Evidence.OpenTimeUtc, observation.Evidence.CloseTimeUtc, now), cancellationToken).ConfigureAwait(false);
    }
}
