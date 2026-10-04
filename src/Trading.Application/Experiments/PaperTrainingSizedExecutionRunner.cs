using Microsoft.Extensions.Logging;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
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

public static class PaperProtectivePositionEvidence
{
    public static DateTimeOffset? OpenedAt(ExperimentWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
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

    public static ExperimentPaperPlanEvidence? FindPlan(
        ExperimentWorker worker, DateTimeOffset openedAt, IReadOnlyList<ExperimentPaperPlanEvidence> plans)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(plans);
        return plans
            .Where(candidate => candidate.DecisionKey.Symbol.Equals(worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
                && candidate.DecisionKey.AsOfUtc <= openedAt
                && candidate.ProtectiveStopPrice > 0m)
            .OrderByDescending(candidate => candidate.DecisionKey.AsOfUtc)
            .ThenByDescending(candidate => candidate.RecordedAtUtc)
            .FirstOrDefault();
    }
}

/// <summary>
/// Integrates approved research plans with the fixed paper-only sizing and execution boundary.
/// It owns no exchange, credentials, live route, futures adapter, or network dependency.
/// </summary>
public sealed class PaperTrainingSizedExecutionRunner : IExperimentWorkerRunner
{
    private static readonly TimeSpan AdmissionEvidencePreparationWindow = TimeSpan.FromMinutes(5);
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
    private readonly IPaperTrainingActivationReader _activations;
    private readonly IExperimentPaperExecutionLedger _executions;
    private readonly TimeProvider _time;
    private readonly ILogger<PaperTrainingSizedExecutionRunner>? _logger;
    private readonly IPaperWorkerAdmissionLimit _admissionLimit;

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
        IPaperTrainingActivationReader activations,
        TimeProvider time,
        IExperimentPaperExecutionLedger executions,
        ILogger<PaperTrainingSizedExecutionRunner>? logger = null,
        IPaperWorkerAdmissionLimit? admissionLimit = null)
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
        _activations = activations ?? throw new ArgumentNullException(nameof(activations));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _logger = logger;
        _admissionLimit = admissionLimit ?? new LegacyPaperWorkerAdmissionLimit();
    }

    public async Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var now = _time.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || worker.Status != ExperimentWorkerStatus.Running)
            return;
        var activation = await _activations.GetAsync(worker.UserId, cancellationToken).ConfigureAwait(false);
        if (activation?.IsActive != true)
            return;
        if (await _executions.HasUnresolvedAsync(worker.UserId, worker.Id, cancellationToken).ConfigureAwait(false))
        {
            ReportSkip(worker, "execution: a prior paper execution requires manual reconciliation");
            return;
        }
        if (worker.PositionQuantity > 0m)
        {
            var openedAt = PaperProtectivePositionEvidence.OpenedAt(worker);
            if (openedAt is null || PaperProtectivePositionEvidence.FindPlan(worker, openedAt.Value,
                await _plans.ListAsync(worker.UserId, worker.Id, cancellationToken).ConfigureAwait(false)) is null)
            {
                ReportSkip(worker, "protection: the open position has no matching approved protective plan");
                return;
            }
        }
        var configuration = await _configurations.GetAsync(worker.UserId, cancellationToken).ConfigureAwait(false);
        var assignment = configuration?.Assignments.SingleOrDefault(x => x.WorkerId == worker.Id);
        if (configuration is null || assignment is null || !configuration.IsRunnableFor(worker, assignment))
        {
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        var observation = await _analysis.AnalyzeAcrossPaperTimeframesAsync(
            worker, configuration, assignment, now, cancellationToken).ConfigureAwait(false);
        if (observation.Evidence is null || observation.Outcome == ExperimentAnalysisOutcome.Blocked)
        {
            ReportSkip(worker, $"analysis: {observation.Reason}");
            if (observation.Reason.Contains(
                    "Closed candle evidence is unavailable",
                    StringComparison.Ordinal)
                && now - worker.CreatedAtUtc < AdmissionEvidencePreparationWindow)
            {
                return;
            }
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
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
        {
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }
        var maximum = await _admissionLimit
            .GetMaximumAsync(worker.UserId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (maximum < 1 || activation.Slots.Count > maximum)
        {
            ReportSkip(worker, "entitlement: new and position-increasing paper entries are disabled");
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (assignment.Provenance.PairFilters is not
            { PriceTick: > 0m, QuantityStep: > 0m, MinimumQuantity: > 0m, MinimumNotional: > 0m })
        {
            ReportSkip(worker, "sizing: the reserved pair lacks approved venue filters; a fresh scanner admission is required");
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        var snapshot = await _snapshots.GetAsync(worker, configuration, assignment, observation, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            ReportSkip(worker, "sizing: waiting for a later closed one-minute paper execution candle or valid plan");
            if (now - evidence.AsOfUtc < AdmissionEvidencePreparationWindow)
                return;
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!IsExactPlan(snapshot.Plan, worker, observation.Evidence,
                snapshot.Context.Candle, snapshot.Context.ExecutionCandle))
        {
            ReportSkip(worker, "sizing: no exact approved paper sizing snapshot was available");
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        var candle = snapshot.Context.ExecutionCandle!;
        if (worker.PositionQuantity > 0m)
        {
            if (candle.ClosePrice <= worker.AverageEntryPrice)
                return;
            worker.RecordFavorablePaperMark(candle.ClosePrice);
        }

        var sizing = snapshot.SizingInput;
        if (sizing.EntryPrice != candle.ClosePrice || sizing.ProtectiveStopPrice != snapshot.Plan.ProtectiveStopPrice
            || sizing.Direction != PaperPositionDirection.Long
            || sizing.EstimatedEntryFeeRate != _paper.EstimatedTakerFeeRate
            || sizing.ExchangeFilters != assignment.Provenance.PairFilters
            || sizing.MarketSnapshotAtUtc != candle.CloseTimeUtc
            || snapshot.Context.Portfolio.PositionQuantity != worker.PositionQuantity)
        {
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }
        var sized = PaperRiskPositionSizer.Size(sizing);
        if (!sized.IsAccepted)
        {
            ReportSkip(worker, $"sizing: {sized.Explanation}");
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fill = snapshot.WorkerRiskRequest.ProposedFill;
        if (fill is null || fill.RequestedQuantity != sized.Quantity || fill.ReferencePrice != candle.ClosePrice
            || fill.OccurredAtUtc != candle.CloseTimeUtc
            || snapshot.WorkerRiskRequest.MarketDataAsOfUtc != candle.CloseTimeUtc)
        {
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!_workerRisk.Evaluate(snapshot.WorkerRiskRequest).IsAllowed)
        {
            ReportSkip(worker, "risk: the worker risk evaluator denied the proposed paper fill");
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        _lastSkipReasons.TryRemove(worker.Id, out _);
        activation = await _activations.GetAsync(worker.UserId, cancellationToken).ConfigureAwait(false);
        if (activation?.IsActive != true)
            return;
        var submissionTime = _time.GetUtcNow();
        if (submissionTime.Offset != TimeSpan.Zero || submissionTime < candle.CloseTimeUtc
            || submissionTime - candle.CloseTimeUtc > TimeSpan.FromMinutes(1))
        {
            ReportSkip(worker, "execution: the later paper reference candle is no longer fresh");
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }
        maximum = await _admissionLimit
            .GetMaximumAsync(worker.UserId, submissionTime, cancellationToken).ConfigureAwait(false);
        if (maximum < 1 || activation.Slots.Count > maximum)
        {
            ReportSkip(worker, "entitlement: paper entry capacity changed before simulated submission");
            await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _plans.SaveAsync(new ExperimentPaperPlanEvidence(
            decision.Key, snapshot.Plan.ProtectiveStopPrice, snapshot.Plan.ConservativeTargetPrice, now), cancellationToken).ConfigureAwait(false);
        // The pipeline receives the sizer output verbatim; it is never clamped or rounded here.
        var result = await _paper.ProcessSizedAsync(decision, snapshot.Context, sized.Quantity, cancellationToken).ConfigureAwait(false);
        if (result.PipelineResult?.Executed != true || result.PaperFill is null)
        {
            if (result.PipelineResult?.RequiresReconciliation != true)
                await CompleteUnfilledScannerAdmissionAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!result.WorkerStatePersisted)
        {
            var adapterFill = result.PaperFill;
            worker.ApplyPaperTrade(adapterFill.Quantity, adapterFill.Price, adapterFill.Fees,
                adapterFill.Direction == Trading.Domain.Execution.TradeDirection.Buy ? "buy" : "sell",
                adapterFill.ExecutedAtUtc, adapterFill.ExecutionCommandId);
            await _ledger.AddAsync(worker.UserId, worker.Ledger.Last(), cancellationToken).ConfigureAwait(false);
            await _workers.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteUnfilledScannerAdmissionAsync(
        ExperimentWorker worker,
        CancellationToken cancellationToken)
    {
        if (worker.PositionQuantity != 0m
            || !worker.Name.StartsWith("Paper opportunity ", StringComparison.Ordinal)
            || worker.Status != ExperimentWorkerStatus.Running)
        {
            return;
        }
        if (await _executions.HasUnresolvedAsync(worker.UserId, worker.Id, cancellationToken).ConfigureAwait(false))
            return;

        worker.Complete();
        await _workers.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
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

    private static bool IsExactPlan(PaperExecutionPlan plan, ExperimentWorker worker,
        ExperimentDecisionEvidence evidence, ExperimentPaperCandleSnapshot candle,
        ExperimentPaperCandleSnapshot? execution)
    {
        if (plan is null || plan.Direction != StrategyAnalysisDirection.Bullish || plan.AsOfUtc != evidence.AsOfUtc
            || plan.EntryReferencePrice != candle.ClosePrice || plan.ProtectiveStopPrice <= 0m
            || !string.Equals(candle.Symbol, worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
            || candle.AsOfUtc != evidence.AsOfUtc || candle.OpenTimeUtc != evidence.OpenTimeUtc || candle.CloseTimeUtc != evidence.CloseTimeUtc
            || candle.Interval != evidence.Interval || string.IsNullOrWhiteSpace(plan.Provenance.PlanProfileId)
            || plan.Provenance.PlanProfileVersion <= 0 || string.IsNullOrWhiteSpace(plan.Provenance.ResearchEvidenceId)
            || execution is null || !string.Equals(execution.Symbol, worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
            || execution.Interval != CandleInterval.OneMinute || execution.OpenTimeUtc != evidence.CloseTimeUtc
            || execution.CloseTimeUtc != evidence.CloseTimeUtc.AddMinutes(1)
            || execution.AsOfUtc != execution.CloseTimeUtc || execution.ClosePrice <= plan.ProtectiveStopPrice
            || execution.QualityFlags is { Count: > 0 })
            return false;
        if (evidence.StrategyId == "platform.relative-strength-pullback-rotation"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.donchian-breakout-ensemble"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.bollinger-mean-reversion"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.rsi-pullback"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.macd-volume"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.ema-trend-continuation"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.volatility-compression-breakout"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.cross-sectional-momentum-rotation"
            && evidence.StrategyVersion >= 4 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.three-swing-channel-divergence"
            && evidence.StrategyVersion >= 4 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        if (evidence.StrategyId == "platform.regime-switching-ensemble"
            && evidence.StrategyVersion >= 5 && plan.Provenance.PlanProfileVersion != 2)
            return false;
        return plan.SourceCandles.Any(source => string.Equals(source.Symbol, candle.Symbol, StringComparison.OrdinalIgnoreCase)
            && source.Interval == candle.Interval && source.OpenTimeUtc == candle.OpenTimeUtc && source.CloseTimeUtc == candle.CloseTimeUtc);
    }
}
