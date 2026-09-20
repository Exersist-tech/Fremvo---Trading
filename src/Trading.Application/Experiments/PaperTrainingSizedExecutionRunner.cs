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
/// Integrates approved research plans with the fixed paper-only sizing and execution boundary.
/// It owns no exchange, credentials, live route, futures adapter, or network dependency.
/// </summary>
public sealed class PaperTrainingSizedExecutionRunner : IExperimentWorkerRunner
{
    private readonly PaperExperimentWorkerRunner _analysis;
    private readonly IExperimentResearchGroupConfigurationSource _configurations;
    private readonly IPaperTrainingSizingSnapshotSource _snapshots;
    private readonly ExperimentDecisionPolicy _decisions;
    private readonly ExperimentWorkerRiskEvaluator _workerRisk;
    private readonly PaperExperimentTradeOrchestrator _paper;
    private readonly TimeProvider _time;

    public PaperTrainingSizedExecutionRunner(
        PaperExperimentWorkerRunner analysis,
        IExperimentResearchGroupConfigurationSource configurations,
        IPaperTrainingSizingSnapshotSource snapshots,
        ExperimentDecisionPolicy decisions,
        ExperimentWorkerRiskEvaluator workerRisk,
        PaperExperimentTradeOrchestrator paper,
        TimeProvider time)
    {
        _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        _configurations = configurations ?? throw new ArgumentNullException(nameof(configurations));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _workerRisk = workerRisk ?? throw new ArgumentNullException(nameof(workerRisk));
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        _time = time ?? throw new ArgumentNullException(nameof(time));
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

        var observation = await _analysis.AnalyzeAsync(worker, configuration, assignment, now, cancellationToken).ConfigureAwait(false);
        if (observation.Outcome != ExperimentAnalysisOutcome.Analyzed || observation.Evidence is null)
            return;

        var snapshot = await _snapshots.GetAsync(worker, configuration, assignment, observation, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !IsExactPlan(snapshot.Plan, worker, observation.Evidence, snapshot.Context.Candle))
            return;

        var sizing = snapshot.SizingInput;
        if (sizing.EntryPrice != snapshot.Plan.EntryReferencePrice || sizing.ProtectiveStopPrice != snapshot.Plan.ProtectiveStopPrice
            || sizing.Direction != PaperPositionDirection.Long || snapshot.Context.Portfolio.PositionQuantity != worker.PositionQuantity)
            return;
        var sized = PaperRiskPositionSizer.Size(sizing);
        if (!sized.IsAccepted)
            return;

        var candle = snapshot.Context.Candle;
        var identity = new ExperimentClosedCandleIdentity(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc, candle.AsOfUtc);
        var decision = await _decisions.DecideAsync(worker, configuration, assignment, observation, snapshot.Context.Portfolio, identity, cancellationToken).ConfigureAwait(false);
        if (decision.Proposal.Action != ExperimentProposalAction.Open)
            return;

        var fill = snapshot.WorkerRiskRequest.ProposedFill;
        if (fill is null || fill.RequestedQuantity != sized.Quantity || fill.ReferencePrice != snapshot.Plan.EntryReferencePrice)
            return;
        if (!_workerRisk.Evaluate(snapshot.WorkerRiskRequest).IsAllowed)
            return;

        // The pipeline receives the sizer output verbatim; it is never clamped or rounded here.
        await _paper.ProcessSizedAsync(decision, snapshot.Context, sized.Quantity, cancellationToken).ConfigureAwait(false);
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
