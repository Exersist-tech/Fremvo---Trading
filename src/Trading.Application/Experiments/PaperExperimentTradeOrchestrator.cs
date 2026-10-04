using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Trading.Application.Pipeline;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;

namespace Trading.Application.Experiments;

/// <summary>
/// A closed-candle market snapshot supplied to the experimental paper path. It intentionally has
/// no account, route, exchange, or mode fields.
/// </summary>
public sealed record ExperimentPaperCandleSnapshot(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset CloseTimeUtc,
    DateTimeOffset AsOfUtc,
    decimal ClosePrice,
    decimal Volume,
    IReadOnlyCollection<string>? QualityFlags = null);

public sealed record ExperimentPaperWorkerContext(
    ExperimentWorker Worker,
    ExperimentWorkerPortfolioSnapshot Portfolio,
    ExperimentPaperCandleSnapshot Candle,
    ExperimentPaperCandleSnapshot? ExecutionCandle = null);

public enum ExperimentPaperExecutionStatus { Claimed = 0, Completed, Blocked, Unknown }

/// <summary>
/// A durable association is claimed before the pipeline is entered. A claimed or unknown proposal
/// is never submitted again: recovery is deliberately manual rather than risking a duplicate trade.
/// </summary>
public sealed record ExperimentPaperExecutionAssociation(
    ExperimentDecisionKey DecisionKey,
    string CorrelationId,
    ExperimentPaperExecutionStatus Status,
    Guid? ExecutionCommandId = null,
    string? Detail = null);

public enum ExperimentPaperExecutionClaimResult { Claimed = 0, Existing, Conflict }

public interface IExperimentPaperExecutionLedger
{
    Task<(ExperimentPaperExecutionClaimResult Result, ExperimentPaperExecutionAssociation? Association)> ClaimAsync(
        Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default);
    Task CompleteAsync(
        Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default);
    Task<bool> HasUnresolvedAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default);
    Task<ExperimentPaperExecutionAssociation?> GetUnresolvedAsync(
        Guid userId, Guid workerId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryExperimentPaperExecutionLedger : IExperimentPaperExecutionLedger
{
    private readonly ConcurrentDictionary<ExperimentDecisionKey, ExperimentPaperExecutionAssociation> _records = new();
    private readonly object _sync = new();

    public Task<(ExperimentPaperExecutionClaimResult Result, ExperimentPaperExecutionAssociation? Association)> ClaimAsync(
        Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (userId == Guid.Empty || association.DecisionKey.UserId != userId)
            throw new InvalidOperationException("Paper execution associations must be claimed by their owner.");
        if (association.Status != ExperimentPaperExecutionStatus.Claimed)
            throw new ArgumentException("A new paper execution association must be Claimed.", nameof(association));

        lock (_sync)
        {
            if (_records.TryGetValue(association.DecisionKey, out var existing))
                return Task.FromResult((
                    string.Equals(existing.CorrelationId, association.CorrelationId, StringComparison.Ordinal)
                        ? ExperimentPaperExecutionClaimResult.Existing
                        : ExperimentPaperExecutionClaimResult.Conflict,
                    (ExperimentPaperExecutionAssociation?)existing));
            if (_records.Values.Any(value => value.DecisionKey.UserId == userId
                && value.DecisionKey.WorkerId == association.DecisionKey.WorkerId
                && value.Status is ExperimentPaperExecutionStatus.Claimed or ExperimentPaperExecutionStatus.Unknown))
                return Task.FromResult((ExperimentPaperExecutionClaimResult.Conflict, (ExperimentPaperExecutionAssociation?)null));

            _records[association.DecisionKey] = association;
            return Task.FromResult((ExperimentPaperExecutionClaimResult.Claimed, (ExperimentPaperExecutionAssociation?)association));
        }
    }

    public Task CompleteAsync(Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (association.Status is not (ExperimentPaperExecutionStatus.Completed
            or ExperimentPaperExecutionStatus.Blocked or ExperimentPaperExecutionStatus.Unknown))
            throw new ArgumentException("A paper execution claim needs a valid terminal outcome.", nameof(association));
        lock (_sync)
        {
            if (association.DecisionKey.UserId != userId
                || !_records.TryGetValue(association.DecisionKey, out var current)
                || !string.Equals(current.CorrelationId, association.CorrelationId, StringComparison.Ordinal)
                || current.Status != ExperimentPaperExecutionStatus.Claimed)
                throw new InvalidOperationException("Only the owner of an unresolved Claimed execution may record its first outcome.");
            _records[association.DecisionKey] = association;
        }
        return Task.CompletedTask;
    }

    public Task<bool> HasUnresolvedAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_records.Values.Any(value =>
            value.DecisionKey.UserId == userId && value.DecisionKey.WorkerId == workerId
            && value.Status is ExperimentPaperExecutionStatus.Claimed or ExperimentPaperExecutionStatus.Unknown));
    }

    public Task<ExperimentPaperExecutionAssociation?> GetUnresolvedAsync(
        Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userId == Guid.Empty || workerId == Guid.Empty)
            throw new ArgumentException("An owner and worker are required.");
        return Task.FromResult(_records.Values.SingleOrDefault(value =>
            value.DecisionKey.UserId == userId && value.DecisionKey.WorkerId == workerId
            && value.Status is ExperimentPaperExecutionStatus.Claimed or ExperimentPaperExecutionStatus.Unknown));
    }
}

public sealed class ExperimentPaperTradeResult
{
    private ExperimentPaperTradeResult(bool submitted, string reason, TradePipelineResult? pipelineResult, PaperExecutionLedgerEntry? paperFill, bool workerStatePersisted)
    {
        Submitted = submitted;
        Reason = reason;
        PipelineResult = pipelineResult;
        PaperFill = paperFill;
        WorkerStatePersisted = workerStatePersisted;
    }

    public bool Submitted { get; }
    public string Reason { get; }
    public TradePipelineResult? PipelineResult { get; }
    /// <summary>Fake paper-adapter result, present only after the mandatory pipeline completed.</summary>
    public PaperExecutionLedgerEntry? PaperFill { get; }
    /// <summary>True when the orchestrator durably applied the fill to the supplied worker.</summary>
    public bool WorkerStatePersisted { get; }
    internal static ExperimentPaperTradeResult Skipped(string reason) => new(false, reason, null, null, false);
    internal static ExperimentPaperTradeResult Processed(TradePipelineResult result, PaperExecutionLedgerEntry? paperFill, bool workerStatePersisted) =>
        new(true, string.Empty, result, paperFill, workerStatePersisted);
}

/// <summary>
/// The only experimental route to paper execution. It accepts a decision already durably recorded
/// by <see cref="IExperimentDecisionLedger"/>, claims that exact decision/candle before invoking
/// <see cref="TradePipeline"/>, and structurally fixes the route to paper execution.
/// Host registration is intentionally omitted until a future training activation.
/// </summary>
public sealed class PaperExperimentTradeOrchestrator
{
    private readonly IExperimentDecisionLedger _decisions;
    private readonly IExperimentPaperExecutionLedger _executions;
    private readonly TradePipeline _pipeline;
    private readonly PaperExecutionAdapter _paperAdapter;
    private readonly decimal _openQuantity;
    private readonly IPaperTradingLedgerRepository? _workerLedger;
    private readonly IExperimentWorkerRepository? _workers;

    public PaperExperimentTradeOrchestrator(
        IExperimentDecisionLedger decisions,
        IExperimentPaperExecutionLedger executions,
        TradePipeline pipeline,
        PaperExecutionAdapter paperAdapter,
        decimal openQuantity = 1m,
        IPaperTradingLedgerRepository? workerLedger = null,
        IExperimentWorkerRepository? workers = null)
    {
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _paperAdapter = paperAdapter ?? throw new ArgumentNullException(nameof(paperAdapter));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(openQuantity, 0m);
        _openQuantity = openQuantity;
        _workerLedger = workerLedger;
        _workers = workers;
    }

    public decimal EstimatedTakerFeeRate => _paperAdapter.EstimatedTakerFeeRate;

    public async Task<ExperimentPaperTradeResult> ProcessAsync(
        ExperimentDecisionRecord proposal,
        ExperimentPaperWorkerContext context,
        CancellationToken cancellationToken = default)
        => await ProcessCoreAsync(proposal, context, null, null, cancellationToken).ConfigureAwait(false);

    internal async Task<ExperimentPaperTradeResult> ProcessPreclaimedProtectiveExitAsync(
        ExperimentDecisionRecord proposal,
        ExperimentPaperWorkerContext context,
        ExperimentPaperExecutionAssociation protectiveClaim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(protectiveClaim);
        if (proposal.Proposal.Action != ExperimentProposalAction.Close
            || protectiveClaim.Status != ExperimentPaperExecutionStatus.Claimed
            || protectiveClaim.DecisionKey.UserId != proposal.Key.UserId
            || protectiveClaim.DecisionKey.WorkerId != proposal.Key.WorkerId
            || protectiveClaim.DecisionKey.StrategyId != "experiment-protective-exit-claim"
            || protectiveClaim.DecisionKey.Symbol != "protective-exit"
            || protectiveClaim.CorrelationId !=
                $"paper-protective-exit-{protectiveClaim.DecisionKey.StrategyFingerprint}"
            || protectiveClaim.DecisionKey.CloseTimeUtc > proposal.Key.CloseTimeUtc
            || proposal.Key.StrategyId != "experiment-protective-exit")
            throw new InvalidOperationException("Only the matching claimed protective exit may share a paper execution claim.");
        return await ProcessCoreAsync(proposal, context, null, protectiveClaim, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> IsPreclaimedExecutionPendingAsync(
        ExperimentPaperExecutionAssociation claim,
        CancellationToken cancellationToken = default) =>
        await _executions.GetUnresolvedAsync(
            claim.DecisionKey.UserId, claim.DecisionKey.WorkerId, cancellationToken).ConfigureAwait(false)
            == claim;

    /// <summary>Submits an opening or add decision using the exact already-approved sizer output.</summary>
    public async Task<ExperimentPaperTradeResult> ProcessSizedAsync(
        ExperimentDecisionRecord proposal,
        ExperimentPaperWorkerContext context,
        decimal exactBuyQuantity,
        CancellationToken cancellationToken = default)
        => await ProcessCoreAsync(proposal, context, exactBuyQuantity, null, cancellationToken).ConfigureAwait(false);

    private async Task<ExperimentPaperTradeResult> ProcessCoreAsync(
        ExperimentDecisionRecord proposal,
        ExperimentPaperWorkerContext context,
        decimal? exactBuyQuantity,
        ExperimentPaperExecutionAssociation? preclaimed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var laterEntry = exactBuyQuantity is not null
            && proposal.Proposal.Action is ExperimentProposalAction.Open or ExperimentProposalAction.Add;
        ValidateContext(proposal, context, laterEntry);

        // The provided record is not trusted merely because it has the right shape: it must be
        // the immutable accepted record in the owner-scoped decision ledger.
        var accepted = await _decisions.ListAsync(proposal.Key.UserId, proposal.Key.WorkerId, cancellationToken).ConfigureAwait(false);
        if (!accepted.Any(record => record == proposal))
            return ExperimentPaperTradeResult.Skipped("Proposal is not an accepted decision-ledger record.");

        if (proposal.Proposal.Action == ExperimentProposalAction.Neutral)
            return ExperimentPaperTradeResult.Skipped("No actionable experimental condition.");
        if (proposal.Proposal.Action is not (ExperimentProposalAction.Open or ExperimentProposalAction.Add
            or ExperimentProposalAction.Reduce or ExperimentProposalAction.Close))
            return ExperimentPaperTradeResult.Skipped("Experimental proposal action is not permitted.");
        if (exactBuyQuantity is not null
            && proposal.Proposal.Action is ExperimentProposalAction.Open or ExperimentProposalAction.Add
            && exactBuyQuantity is not > 0m)
            return ExperimentPaperTradeResult.Skipped("Opening and add paper proposals require an exact approved sizing quantity.");
        ExperimentPaperExecutionAssociation claim;
        if (preclaimed is not null)
        {
            if (!await IsPreclaimedExecutionPendingAsync(preclaimed, cancellationToken).ConfigureAwait(false))
                return ExperimentPaperTradeResult.Skipped("The protective execution claim is not the worker's only unresolved order.");
            claim = preclaimed;
        }
        else
        {
            if (await _executions.HasUnresolvedAsync(proposal.Key.UserId, proposal.Key.WorkerId, cancellationToken)
                    .ConfigureAwait(false))
                return ExperimentPaperTradeResult.Skipped("A prior paper execution requires reconciliation before this worker may submit another order.");
            claim = new ExperimentPaperExecutionAssociation(proposal.Key,
                $"paper-experiment-{Fingerprint(proposal.Key)}", ExperimentPaperExecutionStatus.Claimed);
            var claimed = await _executions.ClaimAsync(proposal.Key.UserId, claim, cancellationToken).ConfigureAwait(false);
            if (claimed.Result != ExperimentPaperExecutionClaimResult.Claimed)
                return ExperimentPaperTradeResult.Skipped(claimed.Result == ExperimentPaperExecutionClaimResult.Conflict
                    ? "Conflicting durable execution association."
                    : $"Proposal has already been claimed with status {claimed.Association?.Status} and will not be retried.");
        }

        TradePipelineResult result;
        try
        {
            var executionCandle = laterEntry ? context.ExecutionCandle! : context.Candle;
            var marketEvent = new MarketEvent(
                GuidFromFingerprint(Fingerprint(proposal.Key)),
                executionCandle.Symbol,
                executionCandle.Interval,
                executionCandle.CloseTimeUtc,
                executionCandle.ClosePrice,
                executionCandle.Volume,
                isClosed: true,
                executionCandle.QualityFlags);
            result = await _pipeline.ProcessAsync(
                marketEvent,
                new PipelineContext(proposal.Key.UserId, TradingMode.Paper, claim.CorrelationId),
                new ProposalStrategy(proposal, context.Portfolio.PositionQuantity, exactBuyQuantity ?? _openQuantity),
                ToPipelinePortfolio(context, executionCandle),
                _paperAdapter,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Persisted Claimed is intentionally terminal until investigated. Retrying after an
            // interrupted call could submit a second paper command if the interruption was late.
            throw;
        }

        var paperFill = result.Executed && result.ExecutionCommandId is Guid executionCommandId
            ? _paperAdapter.FindFill(executionCommandId)
            : null;
        if (result.Executed && paperFill is null)
            throw new InvalidOperationException("A successful paper pipeline execution is missing its simulated fill.");

        var workerStatePersisted = false;
        if (result.Executed && _workerLedger is not null && _workers is not null)
        {
            context.Worker.ApplyPaperTrade(
                paperFill!.Quantity,
                paperFill.Price,
                paperFill.Fees,
                paperFill.Direction == TradeDirection.Buy ? "buy" : "sell",
                paperFill.ExecutedAtUtc,
                paperFill.ExecutionCommandId);
            if (paperFill.Direction == TradeDirection.Sell
                && context.Worker.PositionQuantity == 0m
                && context.Worker.Status != ExperimentWorkerStatus.Failed
                && context.Worker.Name.StartsWith("Paper opportunity ", StringComparison.Ordinal))
            {
                context.Worker.Complete();
            }
            await _workerLedger.AddAsync(
                context.Worker.UserId,
                context.Worker.Ledger.Last(),
                cancellationToken).ConfigureAwait(false);
            await _workers.SaveAsync(context.Worker, cancellationToken).ConfigureAwait(false);
            workerStatePersisted = true;
        }

        var status = result.RequiresReconciliation ? ExperimentPaperExecutionStatus.Unknown
            : result.Executed ? ExperimentPaperExecutionStatus.Completed
            : ExperimentPaperExecutionStatus.Blocked;
        await _executions.CompleteAsync(
            proposal.Key.UserId,
            claim with { Status = status, ExecutionCommandId = result.ExecutionCommandId, Detail = result.BlockedReason },
            cancellationToken).ConfigureAwait(false);
        return ExperimentPaperTradeResult.Processed(result, paperFill, workerStatePersisted);
    }

    private static PortfolioSnapshot ToPipelinePortfolio(
        ExperimentPaperWorkerContext context, ExperimentPaperCandleSnapshot executionCandle) =>
        new(checked(context.Portfolio.PositionQuantity * executionCandle.ClosePrice),
            context.Portfolio.PositionQuantity, context.Worker.CashBalance,
            context.Worker.RealizedProfitAndLoss, 0,
            context.Portfolio.PositionQuantity > 0m ? 1 : 0, context.Portfolio.AsOfUtc);

    private static void ValidateContext(ExperimentDecisionRecord proposal, ExperimentPaperWorkerContext context, bool laterEntry)
    {
        var key = proposal.Key;
        var execution = context.ExecutionCandle;
        if (key.UserId == Guid.Empty || context.Worker.UserId != key.UserId || context.Worker.Id != key.WorkerId
            || context.Portfolio.UserId != key.UserId || context.Portfolio.WorkerId != key.WorkerId
            || context.Portfolio.PositionQuantity < 0m
            || context.Portfolio.PositionQuantity != context.Worker.PositionQuantity
            || context.Portfolio.AsOfUtc != (laterEntry ? execution?.CloseTimeUtc : key.AsOfUtc)
            || !string.Equals(context.Worker.MarketSymbol, key.Symbol, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.Candle.Symbol, key.Symbol, StringComparison.OrdinalIgnoreCase)
            || context.Candle.Interval != key.Interval || context.Candle.OpenTimeUtc != key.OpenTimeUtc
            || context.Candle.CloseTimeUtc != key.CloseTimeUtc || context.Candle.AsOfUtc != key.AsOfUtc
            || context.Candle.ClosePrice <= 0m || context.Candle.Volume < 0m
            || context.Candle.OpenTimeUtc.Offset != TimeSpan.Zero || context.Candle.CloseTimeUtc.Offset != TimeSpan.Zero
            || context.Candle.AsOfUtc.Offset != TimeSpan.Zero || context.Candle.OpenTimeUtc >= context.Candle.CloseTimeUtc)
            throw new InvalidOperationException("Worker, portfolio, and closed-candle context must exactly match the attested decision.");
        if (laterEntry && (execution is null
            || !string.Equals(execution.Symbol, key.Symbol, StringComparison.OrdinalIgnoreCase)
            || execution.Interval != CandleInterval.OneMinute
            || execution.OpenTimeUtc != key.CloseTimeUtc
            || execution.CloseTimeUtc != key.CloseTimeUtc.AddMinutes(1)
            || execution.AsOfUtc != execution.CloseTimeUtc
            || execution.OpenTimeUtc.Offset != TimeSpan.Zero
            || execution.CloseTimeUtc.Offset != TimeSpan.Zero
            || execution.ClosePrice <= 0m || execution.Volume < 0m
            || execution.QualityFlags is { Count: > 0 }))
            throw new InvalidOperationException("A sized paper entry requires an exact later safe closed one-minute execution candle.");
    }

    private static string Fingerprint(ExperimentDecisionKey key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", key.UserId, key.WorkerId, key.GroupConfigurationVersion,
            key.Group, key.StrategyId, key.StrategyVersion, key.StrategyFingerprint, key.Symbol, key.Interval,
            key.OpenTimeUtc.UtcTicks, key.CloseTimeUtc.UtcTicks, key.AsOfUtc.UtcTicks))));

    private static Guid GuidFromFingerprint(string fingerprint) => new(Convert.FromHexString(fingerprint)[..16]);

    private sealed class ProposalStrategy : IPipelineStrategy, IPipelineIntentDetailsStrategy
    {
        private readonly ExperimentDecisionRecord _proposal;
        private readonly decimal _positionQuantity;
        private readonly decimal _buyQuantity;
        public ProposalStrategy(ExperimentDecisionRecord proposal, decimal positionQuantity, decimal buyQuantity) =>
            (_proposal, _positionQuantity, _buyQuantity) = (proposal, positionQuantity, buyQuantity);
        public Guid StrategyId => GuidFromFingerprint(Fingerprint(_proposal.Key));
        public StrategyDecision Evaluate(MarketEvent marketEvent) => new(
            Guid.NewGuid(), StrategyId, marketEvent.Symbol,
            _proposal.Proposal.Action is ExperimentProposalAction.Open or ExperimentProposalAction.Add
                ? SignalDirection.Buy
                : SignalDirection.Sell,
            1m, marketEvent.EventTimeUtc, _proposal.Proposal.Reason);
        public PipelineIntentDetails GetIntentDetails(MarketEvent marketEvent, StrategyDecision decision) =>
            _proposal.Proposal.Action switch
            {
                ExperimentProposalAction.Open or ExperimentProposalAction.Add => new(_buyQuantity),
                ExperimentProposalAction.Reduce => new(Math.Min(_buyQuantity, _positionQuantity), ReduceOnly: true),
                ExperimentProposalAction.Close => new(_positionQuantity, ReduceOnly: true, CloseOnly: true),
                _ => throw new InvalidOperationException("Only actionable paper experiment proposals reach intent construction.")
            };
    }
}
