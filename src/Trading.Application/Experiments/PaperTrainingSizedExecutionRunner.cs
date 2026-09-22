using Microsoft.Extensions.Logging;
using Trading.Domain.Experiments;
using Trading.Risk;
using Trading.Strategies;

namespace Trading.Application.Experiments;

/// <summary>
/// Durable, closed-input evidence for the only automatic paper-training execution path.  The
/// host supplies this from audited stores; an absent value is deliberately a no-op.
/// </summary>
public sealed record PaperTrainingSizingSnapshot(
    PaperExecutionPlan Plan,
    ExperimentPaperWorkerContext Context,
    PaperRiskSizingInput SizingInput,
    ExperimentWorkerRiskEvaluationRequest WorkerRiskRequest);

public interface IPaperTrainingSizingSnapshotSource
{
    Task<PaperTrainingSizingSnapshot?> GetAsync(
        ExperimentWorker worker,
        ExperimentResearchGroupConfiguration configuration,
        ExperimentResearchGroupAssignment assignment,
        ExperimentAnalysisResult observation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable approved-plan evidence retained for every accepted opening proposal. Protective
/// exits consume this record instead of recalculating levels from later market data.
/// </summary>
public sealed record ExperimentPaperPlanEvidence(
    ExperimentDecisionKey DecisionKey,
    decimal ProtectiveStopPrice,
    decimal? ConservativeTargetPrice,
    DateTimeOffset RecordedAtUtc);

public interface IExperimentPaperPlanEvidenceRepository
{
    Task SaveAsync(ExperimentPaperPlanEvidence evidence, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExperimentPaperPlanEvidence>> ListAsync(
        Guid userId,
        Guid workerId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Integrates approved research plans with the fixed paper-only sizing and execution boundary.
/// It owns no exchange, credentials, live route, futures adapter, or network dependency.
/// </summary>
public sealed class PaperTrainingSizedExecutionRunner : IExperimentWorkerRunner
{
    private static readonly Action<ILogger, Guid, string, Exception?> s_logSkipped =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(1, "PaperTrainingFillSkipped"),
            "Paper worker {WorkerId} did not advance to a simulated fill. Stage={Reason}.");
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _lastSkipReasons = new();
    private readonly PaperExperimentWorkerRunner _analysis;
    private readonly IExperimentResearchGroupConfigurationSource _configurations;
    private readonly IPaperTrainingSizingSnapshotSource _snapshots;
    private readonly ExperimentDecisionPolicy _decisions;
    private readonly ExperimentWorkerRiskEvaluator _workerRisk;
    private readonly PaperExperimentTradeOrchestrator _paper;
    private readonly IExperimentWorkerRepository _workers;
    private readonly IPaperTradingLedgerRepository _ledger;
    private readonly IExperimentPaperPlanEvidenceRepository _plans;
    private readonly TimeProvider _time;
    private readonly ILogger<PaperTrainingSizedExecutionRunner>? _logger;

    public PaperTrainingSizedExecutionRunner(
        PaperExperimentWorkerRunner analysis,
        IExperimentResearchGroupConfigurationSource configurations,
        IPaperTrainingSizingSnapshotSource snapshots,
        ExperimentDecisionPolicy decisions,
        ExperimentWorkerRiskEvaluator workerRisk,
        PaperExperimentTradeOrchestrator paper,
        IExperimentWorkerRepository workers,
        IPaperTradingLedgerRepository ledger,
        IExperimentPaperPlanEvidenceRepository plans,
        TimeProvider time,
        ILogger<PaperTrainingSizedExecutionRunner>? logger = null)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _workerRisk = workerRisk ?? throw new ArgumentNullException(nameof(workerRisk));
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    public async Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var now = _time.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || worker.Status != ExperimentWorkerStatus.Running)
            return;
        var configuration = await _configurations.GetAsync(worker.UserId, cancellationToken).ConfigureAwait(false);
        var assignment = configuration?.Assignments.SingleOrDefault(x => x.WorkerId == worker.Id);
        if (configuration is null || assignment is null || !configuration.IsRunnableFor(worker, assignment))
            return;

        var observation = await _analysis.AnalyzeAcrossPaperTimeframesAsync(
            worker, configuration, assignment, now, cancellationToken).ConfigureAwait(false);
        if (observation.Evidence is null || observation.Outcome == ExperimentAnalysisOutcome.Blocked)
        {
            ReportSkip(worker, $"analysis: {observation.Reason}");
            return;
        }

        var evidence = observation.Evidence;
        var identity = new ExperimentClosedCandleIdentity(
            evidence.Symbol,
            evidence.Interval,
            evidence.OpenTimeUtc,
            evidence.CloseTimeUtc,
            evidence.AsOfUtc);
        var decision = await _decisions.DecideAsync(
            worker,
            configuration,
            assignment,
            observation,
            new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, worker.PositionQuantity, evidence.AsOfUtc),
            identity,
            cancellationToken).ConfigureAwait(false);
        if (decision.Proposal.Action is not (ExperimentProposalAction.Open or ExperimentProposalAction.Add))
            return;

        var snapshot = await _snapshots.GetAsync(worker, configuration, assignment, observation, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !IsExactPlan(snapshot.Plan, worker, observation.Evidence, snapshot.Context.Candle))
        {
            ReportSkip(worker, "sizing: no exact approved paper sizing snapshot was available");
            return;
        }

        var candle = snapshot.Context.Candle;
        if (worker.PositionQuantity > 0m)
        {
            if (candle.ClosePrice <= worker.AverageEntryPrice)
                return;
            worker.RecordFavorablePaperMark(candle.ClosePrice);
        }

        var sizing = snapshot.SizingInput;
        if (sizing.EntryPrice != snapshot.Plan.EntryReferencePrice || sizing.ProtectiveStopPrice != snapshot.Plan.ProtectiveStopPrice
            || sizing.Direction != PaperPositionDirection.Long || snapshot.Context.Portfolio.PositionQuantity != worker.PositionQuantity)
            return;
        var sized = PaperRiskPositionSizer.Size(sizing);
        if (!sized.IsAccepted)
        {
            ReportSkip(worker, $"sizing: {sized.Explanation}");
            return;
        }

        await _plans.SaveAsync(new ExperimentPaperPlanEvidence(
            decision.Key, snapshot.Plan.ProtectiveStopPrice, snapshot.Plan.ConservativeTargetPrice, now), cancellationToken).ConfigureAwait(false);

        var fill = snapshot.WorkerRiskRequest.ProposedFill;
        if (fill is null || fill.RequestedQuantity != sized.Quantity || fill.ReferencePrice != snapshot.Plan.EntryReferencePrice)
            return;
        if (!_workerRisk.Evaluate(snapshot.WorkerRiskRequest).IsAllowed)
        {
            ReportSkip(worker, "risk: the worker risk evaluator denied the proposed paper fill");
            return;
        }

        _lastSkipReasons.TryRemove(worker.Id, out _);
        // The pipeline receives the sizer output verbatim; it is never clamped or rounded here.
        var result = await _paper.ProcessSizedAsync(decision, snapshot.Context, sized.Quantity, cancellationToken).ConfigureAwait(false);
        if (result.PipelineResult?.Executed != true || result.PaperFill is null)
            return;

        if (!result.WorkerStatePersisted)
        {
            var adapterFill = result.PaperFill;
            worker.ApplyPaperTrade(adapterFill.Quantity, adapterFill.Price, adapterFill.Fees,
                adapterFill.Direction == Trading.Domain.Execution.TradeDirection.Buy ? "buy" : "sell", adapterFill.ExecutedAtUtc);
            await _ledger.AddAsync(worker.UserId, worker.Ledger.Last(), cancellationToken).ConfigureAwait(false);
            await _workers.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReportSkip(ExperimentWorker worker, string reason)
    {
        if (_logger is null || !_lastSkipReasons.TryGetValue(worker.Id, out var previous)
            || !string.Equals(previous, reason, StringComparison.Ordinal))
        {
            _lastSkipReasons[worker.Id] = reason;
            if (_logger is not null)
                s_logSkipped(_logger, worker.Id, reason, null);
        }
    }

    private static bool IsExactPlan(PaperExecutionPlan plan, ExperimentWorker worker, ExperimentDecisionEvidence evidence, ExperimentPaperCandleSnapshot candle)
    {
        if (plan is null || plan.Direction != StrategyAnalysisDirection.Bullish || plan.AsOfUtc != evidence.AsOfUtc
            || plan.EntryReferencePrice != candle.ClosePrice || plan.ProtectiveStopPrice <= 0m
            || !string.Equals(candle.Symbol, worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
            || candle.AsOfUtc != evidence.AsOfUtc || candle.OpenTimeUtc != evidence.OpenTimeUtc || candle.CloseTimeUtc != evidence.CloseTimeUtc
            || candle.Interval != evidence.Interval || string.IsNullOrWhiteSpace(plan.Provenance.PlanProfileId)
            || plan.Provenance.PlanProfileVersion <= 0 || string.IsNullOrWhiteSpace(plan.Provenance.ResearchEvidenceId))
            return false;
        return plan.SourceCandles.Any(source => string.Equals(source.Symbol, candle.Symbol, StringComparison.OrdinalIgnoreCase)
            && source.Interval == candle.Interval && source.OpenTimeUtc == candle.OpenTimeUtc && source.CloseTimeUtc == candle.CloseTimeUtc);
    }
}
