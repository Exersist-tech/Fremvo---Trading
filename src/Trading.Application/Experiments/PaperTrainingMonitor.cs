using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Application.Experiments;

public enum PaperTrainingMonitorQualification
{
    Unknown = 0,
    Qualified,
    Exploration
}

public sealed record PaperTrainingTradeMonitorItem(
    string Direction,
    decimal Quantity,
    decimal ExecutionPrice,
    decimal Fee,
    DateTimeOffset OccurredAtUtc,
    string Symbol);

public sealed record PaperTrainingExecutionEvidence(
    Guid ExecutionCommandId,
    string Outcome,
    decimal FilledQuantity,
    decimal AverageFillPrice,
    decimal Fees,
    DateTimeOffset ExecutedAtUtc);

public sealed record PaperTrainingUnresolvedExecution(
    string Status,
    string StrategyId,
    string Symbol,
    DateTimeOffset DecisionAsOfUtc,
    string CorrelationId,
    Guid? ExecutionCommandId,
    bool CommandEvidenceChecked,
    IReadOnlyList<Guid> RecordedCommandIds,
    IReadOnlyList<Guid> MatchingWorkerLedgerIds,
    bool PortfolioEvidenceChecked,
    IReadOnlyList<Guid> RecordedPortfolioCommandIds,
    bool AuditEvidenceChecked,
    IReadOnlyList<string> RecordedAuditActions,
    bool ExecutionEvidenceChecked,
    IReadOnlyList<PaperTrainingExecutionEvidence> RecordedExecutions,
    IReadOnlyList<string> EvidenceConflicts);

public interface IPaperTradeAuditEvidenceReader
{
    Task<IReadOnlyList<string>> ListActionsByCorrelationAsync(
        Guid ownerId, string correlationId, CancellationToken cancellationToken = default);
}

public sealed record PaperTrainingWorkerMonitorItem(
    int Slot,
    string StrategyId,
    string Symbol,
    CandleInterval Interval,
    IReadOnlyList<CandleInterval> AnalysisIntervals,
    PaperTrainingMonitorQualification Qualification,
    Guid? WorkerId,
    string RuntimeStatus,
    int Seed,
    decimal StartingCash,
    decimal? CashBalance,
    decimal? PositionQuantity,
    decimal? AverageEntryPrice,
    decimal? PositionCost,
    decimal? CurrentPrice,
    DateTimeOffset? CurrentPriceAsOfUtc,
    decimal? PositionMarketValue,
    decimal? UnrealizedProfitAndLoss,
    decimal? RealizedProfitAndLoss,
    int? AdditionCount,
    int? MaximumAdditions,
    int TradeCount,
    string? FailureReason,
    IReadOnlyList<PaperTrainingTradeMonitorItem> RecentTrades,
    string StrategyParameters = "{}",
    PaperTrainingUnresolvedExecution? UnresolvedExecution = null,
    decimal? OpenBuyFillPrice = null,
    decimal? LastBuyFillPrice = null,
    decimal? LastSellFillPrice = null);

public sealed record PaperTrainingMonitor(
    PaperTrainingActivationState State,
    IReadOnlyList<PaperTrainingWorkerMonitorItem> Workers);

/// <summary>
/// Owner-scoped, read-only projection of the currently activated paper workers and their
/// immutable simulated trade ledgers.
/// </summary>
public sealed class PaperTrainingMonitorService
{
    public static readonly TimeSpan MaximumValuationAge = TimeSpan.FromMinutes(10);
    private const int RecentTradeLimit = 10;
    private readonly IPaperTrainingActivationReader _activations;
    private readonly IExperimentWorkerRepository _workers;
    private readonly ICandleRepository _candles;
    private readonly IExperimentPaperExecutionLedger _executions;
    private readonly IExperimentPaperPlanEvidenceRepository _plans;
    private readonly TimeProvider _time;
    private readonly IExecutionCommandRepository? _commands;
    private readonly IPaperExecutionResultRepository? _executionResults;
    private readonly IPortfolioUpdateRepository? _portfolioUpdates;
    private readonly IPaperTradeAuditEvidenceReader? _auditEvidence;

    public PaperTrainingMonitorService(
        IPaperTrainingActivationReader activations,
        IExperimentWorkerRepository workers,
        ICandleRepository candles,
        IExperimentPaperExecutionLedger executions,
        IExperimentPaperPlanEvidenceRepository plans,
        TimeProvider? time = null,
        IExecutionCommandRepository? commands = null,
        IPortfolioUpdateRepository? portfolioUpdates = null,
        IPaperTradeAuditEvidenceReader? auditEvidence = null,
        IPaperExecutionResultRepository? executionResults = null)
    {
        _activations = activations ?? throw new ArgumentNullException(nameof(activations));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _time = time ?? TimeProvider.System;
        _commands = commands;
        _executionResults = executionResults;
        _portfolioUpdates = portfolioUpdates;
        _auditEvidence = auditEvidence;
    }

    public async Task<PaperTrainingMonitor> GetAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("Owner is required.", nameof(ownerId));

        var activation = await _activations.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (activation is null
            || (!activation.IsActive && activation.Slots.Count == 0))
            return new(activation?.State ?? PaperTrainingActivationState.Inactive, []);

        var sessionSuffix = activation.ChangedAtUtc.ToString(
            "yyyyMMddHHmmssfffffff",
            System.Globalization.CultureInfo.InvariantCulture);
        var workerNames = activation.Slots
            .Select(slot => WorkerName(slot, sessionSuffix))
            .ToArray();
        var workers = await _workers
            .ListByNamesAsync(ownerId, workerNames, cancellationToken)
            .ConfigureAwait(false);
        var unresolvedByWorker = new Dictionary<Guid, ExperimentPaperExecutionAssociation>();
        var unprotectedWorkerIds = new HashSet<Guid>();
        foreach (var worker in workers)
        {
            var unresolved = await _executions.GetUnresolvedAsync(ownerId, worker.Id, cancellationToken).ConfigureAwait(false);
            if (unresolved is not null)
            {
                if (unresolved.DecisionKey.UserId != ownerId || unresolved.DecisionKey.WorkerId != worker.Id)
                    throw new InvalidOperationException("Unresolved execution evidence must belong to the requested owner and worker.");
                unresolvedByWorker.Add(worker.Id, unresolved);
            }
            if (worker.PositionQuantity > 0m)
            {
                var opened = PaperProtectivePositionEvidence.OpenedAt(worker);
                if (worker.Status is not (ExperimentWorkerStatus.Running or ExperimentWorkerStatus.Paused or ExperimentWorkerStatus.Failed)
                    || opened is null || PaperProtectivePositionEvidence.FindPlan(worker, opened.Value,
                    await _plans.ListAsync(ownerId, worker.Id, cancellationToken).ConfigureAwait(false)) is null)
                    unprotectedWorkerIds.Add(worker.Id);
            }
        }
        var commandIdsByWorker = new Dictionary<Guid, IReadOnlyList<Guid>>();
        var commandsByWorker = new Dictionary<Guid, IReadOnlyList<ExecutionCommand>>();
        var executionsByWorker = new Dictionary<Guid, IReadOnlyList<PaperExecutionEvidence>>();
        var portfolioCommandIdsByWorker = new Dictionary<Guid, IReadOnlyList<Guid>>();
        var portfolioUpdatesByWorker = new Dictionary<Guid, IReadOnlyList<PortfolioUpdate>>();
        var auditActionsByWorker = new Dictionary<Guid, IReadOnlyList<string>>();
        if (_commands is not null)
        {
            foreach (var (workerId, claim) in unresolvedByWorker)
            {
                var records = await _commands.ListByCorrelationAsync(
                    ownerId, claim.CorrelationId, cancellationToken).ConfigureAwait(false);
                if (records.Any(record => record.Context.UserId != ownerId
                    || record.Context.CorrelationId != claim.CorrelationId
                    || record.Context.Mode != TradingMode.Paper
                    || record.Stage != PipelineStage.ExecutionCommand))
                    throw new InvalidOperationException("Unresolved paper command evidence does not match its owner and correlation.");
                commandIdsByWorker.Add(workerId, records.Select(record => record.Payload.Id).ToArray());
                commandsByWorker.Add(workerId, records.Select(record => record.Payload).ToArray());
            }
        }
        if (_portfolioUpdates is not null)
        {
            foreach (var (workerId, claim) in unresolvedByWorker)
            {
                var records = await _portfolioUpdates.ListByCorrelationAsync(
                    ownerId, claim.CorrelationId, cancellationToken).ConfigureAwait(false);
                if (records.Any(record => record.Context.UserId != ownerId
                    || record.Context.CorrelationId != claim.CorrelationId
                    || record.Context.Mode != TradingMode.Paper
                    || record.Stage != PipelineStage.PortfolioUpdate))
                    throw new InvalidOperationException("Unresolved paper portfolio evidence does not match its owner and correlation.");
                portfolioCommandIdsByWorker.Add(workerId,
                    records.Select(record => record.Payload.ExecutionCommandId).ToArray());
                portfolioUpdatesByWorker.Add(workerId, records.Select(record => record.Payload).ToArray());
            }
        }
        if (_executionResults is not null)
        {
            foreach (var (workerId, claim) in unresolvedByWorker)
            {
                var records = await _executionResults.ListByCorrelationAsync(
                    ownerId, claim.CorrelationId, cancellationToken).ConfigureAwait(false);
                if (records.Any(record => record.Context.UserId != ownerId
                    || record.Context.CorrelationId != claim.CorrelationId
                    || record.Context.Mode != TradingMode.Paper
                    || record.Stage != PipelineStage.Execution))
                    throw new InvalidOperationException("Unresolved paper execution results do not match their owner and correlation.");
                executionsByWorker.Add(workerId, records.Select(record => record.Payload).ToArray());
            }
        }
        if (_auditEvidence is not null)
        {
            foreach (var (workerId, claim) in unresolvedByWorker)
                auditActionsByWorker.Add(workerId,
                    await _auditEvidence.ListActionsByCorrelationAsync(
                        ownerId, claim.CorrelationId, cancellationToken).ConfigureAwait(false));
        }
        var latestPrices = new Dictionary<string, Candle?>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in activation.Slots.Select(slot => slot.Symbol).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            latestPrices[symbol] = await GetLatestClosedPriceAsync(symbol, CandleInterval.FiveMinutes, cancellationToken).ConfigureAwait(false);
        }
        var oneMinutePrices = new Dictionary<string, Candle?>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in workers.Where(worker => worker.PositionQuantity > 0m)
                     .Select(worker => worker.MarketSymbol).Distinct(StringComparer.OrdinalIgnoreCase))
            oneMinutePrices[symbol] = await GetLatestClosedPriceAsync(symbol, CandleInterval.OneMinute, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var reservedItems = activation.Slots
            .OrderBy(slot => slot.Slot)
            .Select(slot =>
            {
                var expectedName = WorkerName(slot, sessionSuffix);
                var worker = workers.SingleOrDefault(candidate =>
                    candidate.Name.Equals(expectedName, StringComparison.Ordinal)
                    && candidate.StrategyId.Equals(slot.StrategyId, StringComparison.Ordinal)
                    && candidate.MarketSymbol.Equals(slot.Symbol, StringComparison.Ordinal));
                var observationPrefix = slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal)
                    ? slot.ProvenanceId[5..]
                    : null;
                var qualification = activation.QualificationResults.LastOrDefault(result =>
                    string.Equals(result.StrategyId, slot.StrategyId, StringComparison.Ordinal)
                    && string.Equals(result.Symbol, slot.Symbol, StringComparison.OrdinalIgnoreCase)
                    && (observationPrefix is not null
                        ? result.DatasetFingerprint.StartsWith(observationPrefix, StringComparison.Ordinal)
                        : result.Slot == slot.Slot));
                var qualificationStatus = qualification?.Accepted == true
                    ? PaperTrainingMonitorQualification.Qualified
                    : qualification?.PaperOnlyExploration == true
                        ? PaperTrainingMonitorQualification.Exploration
                        : slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal)
                            ? PaperTrainingMonitorQualification.Qualified
                        : PaperTrainingMonitorQualification.Unknown;
                latestPrices.TryGetValue(slot.Symbol, out var latestCandle);
                oneMinutePrices.TryGetValue(slot.Symbol, out var exitCandle);
                var protectionDataStale = worker is not null && worker.PositionQuantity > 0m
                    && !unprotectedWorkerIds.Contains(worker.Id) && !unresolvedByWorker.ContainsKey(worker.Id)
                    && (exitCandle is null || exitCandle.CloseTimeUtc > now
                        || now - exitCandle.CloseTimeUtc > TimeSpan.FromMinutes(1));
                var unresolved = worker is not null && unresolvedByWorker.TryGetValue(worker.Id, out var claim)
                    ? claim : null;
                IReadOnlyList<Guid> recordedCommandIds = worker is not null
                    && commandIdsByWorker.TryGetValue(worker.Id, out var ids) ? ids : [];
                IReadOnlyList<PaperExecutionEvidence> executionRows = worker is not null
                    && executionsByWorker.TryGetValue(worker.Id, out var executionEvidence)
                    ? executionEvidence : [];
                var recordedExecutions = executionRows.Select(result => new PaperTrainingExecutionEvidence(
                    result.ExecutionCommandId, result.Outcome.ToString(), result.FilledQuantity,
                    result.AverageFillPrice, result.Fees, result.ExecutedAtUtc)).ToArray();
                IReadOnlyList<Guid> recordedPortfolioCommandIds = worker is not null
                    && portfolioCommandIdsByWorker.TryGetValue(worker.Id, out var portfolioIds)
                    ? portfolioIds : [];
                IReadOnlyList<string> recordedAuditActions = worker is not null
                    && auditActionsByWorker.TryGetValue(worker.Id, out var actions)
                    ? actions : [];
                var claimCommandIds = unresolved?.ExecutionCommandId is Guid claimedId
                    ? recordedCommandIds.Append(claimedId).ToHashSet()
                    : recordedCommandIds.ToHashSet();
                var matchingLedgerIds = worker?.Ledger.Where(entry => claimCommandIds.Contains(entry.Id))
                    .Select(entry => entry.Id).ToArray() ?? [];
                if (exitCandle is { CanBeUsedForClosedCandleSignal: true }
                    && exitCandle.CloseTimeUtc <= now
                    && (latestCandle is not { CanBeUsedForClosedCandleSignal: true }
                        || latestCandle.CloseTimeUtc > now
                        || exitCandle.CloseTimeUtc > latestCandle.CloseTimeUtc))
                    latestCandle = exitCandle;
                decimal? currentPrice = latestCandle is { CanBeUsedForClosedCandleSignal: true }
                    && latestCandle.CloseTimeUtc <= now
                    && now - latestCandle.CloseTimeUtc <= MaximumValuationAge
                    ? latestCandle.Close
                    : null;
                decimal? positionMarketValue = worker is not null && currentPrice is decimal marketPrice
                    ? checked(worker.PositionQuantity * marketPrice)
                    : null;
                decimal? unrealizedProfitAndLoss = worker is not null && currentPrice is decimal latestPrice
                    ? checked(worker.PositionQuantity * (latestPrice - worker.AverageEntryPrice))
                    : null;
                var fillPrices = worker is null
                    ? (OpenBuy: (decimal?)null, LastBuy: (decimal?)null, LastSell: (decimal?)null)
                    : FillPrices(worker);

                return new PaperTrainingWorkerMonitorItem(
                    slot.Slot,
                    slot.StrategyId,
                    slot.Symbol,
                    slot.Interval,
                    AnalysisIntervals(slot),
                    qualificationStatus,
                    worker?.Id,
                    unresolved is not null
                        ? "RequiresReconciliation"
                        : worker is not null && unprotectedWorkerIds.Contains(worker.Id)
                            ? "Unprotected"
                        : protectionDataStale ? "ProtectionDataStale"
                        : worker?.Status.ToString() ?? "WaitingForWorker",
                    slot.Seed,
                    slot.StartingCash,
                    worker?.CashBalance,
                    worker?.PositionQuantity,
                    worker?.AverageEntryPrice,
                    worker is null ? null : worker.PositionQuantity * worker.AverageEntryPrice,
                    currentPrice,
                    latestCandle?.CloseTimeUtc,
                    positionMarketValue,
                    unrealizedProfitAndLoss,
                    worker?.RealizedProfitAndLoss,
                    worker?.AdditionCount,
                    worker?.PositionControls.MaxAdditionsPerPosition,
                    worker?.Ledger.Count ?? 0,
                    unresolved is not null
                        ? "A prior paper execution has an unresolved outcome. Verify the fill, worker ledger, and portfolio before releasing this slot."
                        : worker is not null && unprotectedWorkerIds.Contains(worker.Id)
                            ? "Open paper position lacks an eligible worker state or matching approved protective plan; no automated protective exit can run."
                        : protectionDataStale
                            ? "The one-minute closed-candle reference for paper protection is missing, stale, or future-dated; automatic exits cannot use an old price."
                        : worker?.FailureReason,
                    worker?.Ledger
                        .OrderByDescending(trade => trade.OccurredAtUtc)
                        .ThenByDescending(trade => trade.Id)
                        .Take(RecentTradeLimit)
                        .Select(trade => new PaperTrainingTradeMonitorItem(
                            trade.Direction,
                            trade.Quantity,
                            trade.ExecutionPrice,
                            trade.Fee,
                            trade.OccurredAtUtc,
                            trade.Symbol))
                        .ToArray()
                        ?? [],
                    slot.StrategyParameters,
                    unresolved is null ? null : new PaperTrainingUnresolvedExecution(
                        unresolved.Status.ToString(), unresolved.DecisionKey.StrategyId,
                        unresolved.DecisionKey.Symbol, unresolved.DecisionKey.AsOfUtc,
                        unresolved.CorrelationId, unresolved.ExecutionCommandId,
                        _commands is not null, recordedCommandIds, matchingLedgerIds,
                        _portfolioUpdates is not null, recordedPortfolioCommandIds,
                        _auditEvidence is not null, recordedAuditActions,
                        _executionResults is not null, recordedExecutions,
                        FindEvidenceConflicts(
                            unresolved, worker!,
                            commandsByWorker.GetValueOrDefault(worker!.Id),
                            executionsByWorker.GetValueOrDefault(worker.Id),
                            portfolioUpdatesByWorker.GetValueOrDefault(worker.Id),
                            auditActionsByWorker.GetValueOrDefault(worker.Id))),
                    OpenBuyFillPrice: fillPrices.OpenBuy,
                    LastBuyFillPrice: fillPrices.LastBuy,
                    LastSellFillPrice: fillPrices.LastSell);
            })
            .Where(item => item.PositionQuantity is > 0m
                || item.RuntimeStatus == "RequiresReconciliation"
                || item.RuntimeStatus is not nameof(ExperimentWorkerStatus.Completed)
                    and not nameof(ExperimentWorkerStatus.Failed))
            .ToArray();

        if (!activation.IsActive || activation.ConfiguredStrategies.Count == 0)
            return new(activation.State, reservedItems);

        var reservedSlotNumbers = reservedItems.Select(item => item.Slot).ToHashSet();
        var scanningItems = activation.ConfiguredStrategies
            .Where(assignment => !reservedSlotNumbers.Contains(assignment.Slot))
            .Select(assignment =>
            {
                var template = PaperTrainingActivationService.ApprovedSlots.Single(slot =>
                    slot.StrategyId.Equals(assignment.StrategyId, StringComparison.Ordinal));
                return new PaperTrainingWorkerMonitorItem(
                    Slot: assignment.Slot,
                    StrategyId: assignment.StrategyId,
                    Symbol: string.Empty,
                    Interval: CandleInterval.None,
                    AnalysisIntervals: [],
                    Qualification: PaperTrainingMonitorQualification.Unknown,
                    WorkerId: null,
                    RuntimeStatus: "Scanning",
                    Seed: template.Seed,
                    StartingCash: PaperTrainingActivationService.FixedStartingCash,
                    CashBalance: null,
                    PositionQuantity: null,
                    AverageEntryPrice: null,
                    PositionCost: null,
                    CurrentPrice: null,
                    CurrentPriceAsOfUtc: null,
                    PositionMarketValue: null,
                    UnrealizedProfitAndLoss: null,
                    RealizedProfitAndLoss: null,
                    AdditionCount: null,
                    MaximumAdditions: null,
                    TradeCount: 0,
                    FailureReason: null,
                    RecentTrades: [],
                    StrategyParameters: assignment.StrategyParameters);
            })
            .ToArray();

        return new(
            activation.State,
            reservedItems.Concat(scanningItems).OrderBy(item => item.Slot).ToArray());
    }

    private static List<string> FindEvidenceConflicts(
        ExperimentPaperExecutionAssociation claim,
        ExperimentWorker worker,
        IReadOnlyList<ExecutionCommand>? commands,
        IReadOnlyList<PaperExecutionEvidence>? executions,
        IReadOnlyList<PortfolioUpdate>? updates,
        IReadOnlyList<string>? auditActions)
    {
        var conflicts = new List<string>();
        if (commands is { Count: > 1 })
            conflicts.Add("Multiple commands share this claim's correlation.");
        if (executions is { Count: > 1 })
            conflicts.Add("Multiple execution results share this claim's correlation.");
        if (updates is { Count: > 1 })
            conflicts.Add("Multiple portfolio updates share this claim's correlation.");

        var command = commands is { Count: 1 } ? commands[0] : null;
        var execution = executions is { Count: 1 } ? executions[0] : null;
        var update = updates is { Count: 1 } ? updates[0] : null;
        var commandId = claim.ExecutionCommandId;
        if (commandId is Guid claimedId && (
            command is not null && command.Id != claimedId
            || execution is not null && execution.ExecutionCommandId != claimedId
            || update is not null && update.ExecutionCommandId != claimedId))
            conflicts.Add("Recorded command identity differs from the unresolved claim.");
        if (command is not null && (
            execution is not null && execution.ExecutionCommandId != command.Id
            || update is not null && update.ExecutionCommandId != command.Id))
            conflicts.Add("Pipeline stages reference different execution commands.");
        if (execution is not null && update is not null
            && execution.ExecutionCommandId != update.ExecutionCommandId)
            conflicts.Add("Execution and portfolio updates reference different commands.");

        if (command is not null && !command.Symbol.Equals(worker.MarketSymbol, StringComparison.OrdinalIgnoreCase))
            conflicts.Add("Command symbol differs from the worker's pair.");
        if (execution is not null)
        {
            var ledger = worker.Ledger.SingleOrDefault(entry => entry.Id == execution.ExecutionCommandId);
            if (execution.Outcome == ExecutionOutcome.Rejected)
            {
                if (execution.FilledQuantity > 0m || ledger is not null || update is not null
                    || auditActions?.Contains("Trade.PaperExecuted") == true)
                    conflicts.Add("A rejected outcome has recorded fill, portfolio, or success evidence.");
            }
            else if (execution.Outcome == ExecutionOutcome.Filled)
            {
                if (command is not null && execution.FilledQuantity > command.Quantity)
                    conflicts.Add("Simulated fill exceeds the recorded command quantity.");
                if (ledger is not null && (ledger.Quantity != execution.FilledQuantity
                    || ledger.ExecutionPrice != execution.AverageFillPrice
                    || ledger.Fee != execution.Fees
                    || ledger.OccurredAtUtc != execution.ExecutedAtUtc
                    || command is not null && !ledger.Direction.Equals(
                        command.Direction == TradeDirection.Buy ? "buy" : "sell", StringComparison.OrdinalIgnoreCase)))
                    conflicts.Add("Worker ledger differs from the recorded simulated fill.");
                if (update is not null && command is not null
                    && update.ExecutionCommandId == execution.ExecutionCommandId)
                {
                    var delta = command.Direction == TradeDirection.Buy
                        ? execution.FilledQuantity : -execution.FilledQuantity;
                    var cashDelta = command.Direction == TradeDirection.Buy
                        ? -(execution.FilledQuantity * execution.AverageFillPrice + execution.Fees)
                        : execution.FilledQuantity * execution.AverageFillPrice - execution.Fees;
                    if (update.PositionDelta != delta || update.CashBalanceAfter - update.CashBalanceBefore != cashDelta
                        || update.Fees != execution.Fees
                        || !update.Symbol.Equals(command.Symbol, StringComparison.OrdinalIgnoreCase))
                        conflicts.Add("Portfolio change differs from the recorded simulated fill.");
                }
            }
        }
        return conflicts;
    }

    private static string WorkerName(PaperTrainingWorkerSlot slot, string sessionSuffix) =>
        slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal)
            ? ContinuousPaperOpportunityScanner.WorkerName(slot)
            : $"Paper training {slot.Slot} {sessionSuffix}";

    private static CandleInterval[] AnalysisIntervals(PaperTrainingWorkerSlot slot)
    {
        var profile = ApprovedConsensusStrategyProfiles.TryGet(slot.StrategyId, out var profiles)
            ? profiles.FirstOrDefault(candidate => candidate.Signal == slot.Interval)
            : null;
        return profile is null
            ? [slot.Interval]
            : new[] { profile.Regime, profile.Signal, profile.Execution }.Distinct().ToArray();
    }

    private static (decimal? OpenBuy, decimal? LastBuy, decimal? LastSell) FillPrices(ExperimentWorker worker)
    {
        var quantity = 0m;
        var average = 0m;
        decimal? lastBuy = null;
        decimal? lastSell = null;
        foreach (var fill in worker.Ledger.OrderBy(entry => entry.OccurredAtUtc).ThenBy(entry => entry.Id))
        {
            if (fill.Direction.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                if (quantity == 0m) lastSell = null;
                var nextQuantity = checked(quantity + fill.Quantity);
                average = checked(average * quantity + fill.ExecutionPrice * fill.Quantity) / nextQuantity;
                quantity = nextQuantity;
                lastBuy = fill.ExecutionPrice;
            }
            else
            {
                quantity -= fill.Quantity;
                lastSell = fill.ExecutionPrice;
                if (quantity == 0m) average = 0m;
            }
        }
        if (quantity != worker.PositionQuantity)
            throw new InvalidOperationException("Open paper fill prices do not reconcile with the worker position.");
        return (quantity > 0m ? average : null, lastBuy, lastSell);
    }

    private async Task<Candle?> GetLatestClosedPriceAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken)
    {
        var latest = await _candles
            .GetLatestAsync(symbol, interval, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null || latest.CanBeUsedForClosedCandleSignal)
            return latest;

        var candidates = await _candles.ListAsync(
            symbol,
            interval,
            latest.OpenTimeUtc.AddDays(-1),
            latest.OpenTimeUtc,
            cancellationToken).ConfigureAwait(false);
        return candidates
            .Where(candle => candle.CanBeUsedForClosedCandleSignal)
            .OrderByDescending(candle => candle.CloseTimeUtc)
            .ThenByDescending(candle => candle.OpenTimeUtc)
            .FirstOrDefault();
    }
}
