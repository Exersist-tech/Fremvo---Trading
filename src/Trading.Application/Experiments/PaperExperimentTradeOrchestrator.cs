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
    ExperimentPaperCandleSnapshot Candle);

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
}

public sealed class InMemoryExperimentPaperExecutionLedger : IExperimentPaperExecutionLedger
{
    private readonly ConcurrentDictionary<ExperimentDecisionKey, ExperimentPaperExecutionAssociation> _records = new();

    public Task<(ExperimentPaperExecutionClaimResult Result, ExperimentPaperExecutionAssociation? Association)> ClaimAsync(
        Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (userId == Guid.Empty || association.DecisionKey.UserId != userId)
            throw new InvalidOperationException("Paper execution associations must be claimed by their owner.");

        if (_records.TryAdd(association.DecisionKey, association))
            return Task.FromResult((ExperimentPaperExecutionClaimResult.Claimed, (ExperimentPaperExecutionAssociation?)association));
        var existing = _records[association.DecisionKey];
        return Task.FromResult((
            string.Equals(existing.CorrelationId, association.CorrelationId, StringComparison.Ordinal)
                ? ExperimentPaperExecutionClaimResult.Existing
                : ExperimentPaperExecutionClaimResult.Conflict,
            (ExperimentPaperExecutionAssociation?)existing));
    }

    public Task CompleteAsync(Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (association.DecisionKey.UserId != userId
            || !_records.TryGetValue(association.DecisionKey, out var current)
            || !string.Equals(current.CorrelationId, association.CorrelationId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the owner of an existing claim may complete it.");
        _records[association.DecisionKey] = association;
        return Task.CompletedTask;
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

    public async Task<ExperimentPaperTradeResult> ProcessAsync(
        ExperimentDecisionRecord proposal,
        ExperimentPaperWorkerContext context,
        CancellationToken cancellationToken = default)
        => await ProcessCoreAsync(proposal, context, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Submits an opening decision using the exact already-approved sizer output.</summary>
    public async Task<ExperimentPaperTradeResult> ProcessSizedAsync(
        ExperimentDecisionRecord proposal,
        ExperimentPaperWorkerContext context,
        decimal exactOpenQuantity,
        CancellationToken cancellationToken = default)
        => await ProcessCoreAsync(proposal, context, exactOpenQuantity, cancellationToken).ConfigureAwait(false);

    private async Task<ExperimentPaperTradeResult> ProcessCoreAsync(
        ExperimentDecisionRecord proposal,
        ExperimentPaperWorkerContext context,
        decimal? exactOpenQuantity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateContext(proposal, context);

        // The provided record is not trusted merely because it has the right shape: it must be
        // the immutable accepted record in the owner-scoped decision ledger.
        var accepted = await _decisions.ListAsync(proposal.Key.UserId, proposal.Key.WorkerId, cancellationToken).ConfigureAwait(false);
        if (!accepted.Any(record => record == proposal))
            return ExperimentPaperTradeResult.Skipped("Proposal is not an accepted decision-ledger record.");

        if (proposal.Proposal.Action == ExperimentProposalAction.Neutral)
            return ExperimentPaperTradeResult.Skipped("No actionable experimental condition.");
        if (proposal.Proposal.Action is not (ExperimentProposalAction.Open or ExperimentProposalAction.Reduce or ExperimentProposalAction.Close))
            return ExperimentPaperTradeResult.Skipped("Experimental proposal action is not permitted.");
        if (exactOpenQuantity is not null && proposal.Proposal.Action == ExperimentProposalAction.Open
            && exactOpenQuantity is not > 0m)
            return ExperimentPaperTradeResult.Skipped("Opening paper proposals require an exact approved sizing quantity.");

        var correlationId = $"paper-experiment-{Fingerprint(proposal.Key)}";
        var claim = new ExperimentPaperExecutionAssociation(proposal.Key, correlationId, ExperimentPaperExecutionStatus.Claimed);
        var claimed = await _executions.ClaimAsync(proposal.Key.UserId, claim, cancellationToken).ConfigureAwait(false);
        if (claimed.Result != ExperimentPaperExecutionClaimResult.Claimed)
            return ExperimentPaperTradeResult.Skipped(claimed.Result == ExperimentPaperExecutionClaimResult.Conflict
                ? "Conflicting durable execution association."
                : $"Proposal has already been claimed with status {claimed.Association?.Status} and will not be retried.");

        TradePipelineResult result;
        try
        {
            var marketEvent = new MarketEvent(
                GuidFromFingerprint(Fingerprint(proposal.Key)),
                context.Candle.Symbol,
                context.Candle.Interval,
                context.Candle.CloseTimeUtc,
                context.Candle.ClosePrice,
                context.Candle.Volume,
                isClosed: true,
                context.Candle.QualityFlags);
            result = await _pipeline.ProcessAsync(
                marketEvent,
                new PipelineContext(proposal.Key.UserId, TradingMode.Paper, correlationId),
                new ProposalStrategy(proposal, context.Portfolio.PositionQuantity, exactOpenQuantity ?? _openQuantity),
                ToPipelinePortfolio(context.Portfolio),
                _paperAdapter,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Persisted Claimed is intentionally terminal until investigated. Retrying after an
            // interrupted call could submit a second paper command if the interruption was late.
            throw;
        }

        var workerStatePersisted = false;
        if (result.Executed && _workerLedger is not null && _workers is not null)
        {
            var fill = _paperAdapter.Ledger.LastOrDefault(entry => entry.ExecutionCommandId == result.ExecutionCommandId);
            if (fill is null)
                throw new InvalidOperationException("A successful paper pipeline execution is missing its simulated fill.");

            context.Worker.ApplyPaperTrade(
                fill.Quantity,
                fill.Price,
                fill.Fees,
                fill.Direction == TradeDirection.Buy ? "buy" : "sell",
                fill.ExecutedAtUtc);
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
        var paperFill = result.Executed && result.ExecutionCommandId is Guid executionCommandId
            ? _paperAdapter.Ledger.SingleOrDefault(entry => entry.ExecutionCommandId == executionCommandId)
            : null;
        return ExperimentPaperTradeResult.Processed(result, paperFill, workerStatePersisted);
    }

    private static PortfolioSnapshot ToPipelinePortfolio(ExperimentWorkerPortfolioSnapshot portfolio) =>
        new(portfolio.PositionQuantity, portfolio.PositionQuantity, decimal.MaxValue / 100m, 0m, 0, portfolio.PositionQuantity > 0m ? 1 : 0, portfolio.AsOfUtc);

    private static void ValidateContext(ExperimentDecisionRecord proposal, ExperimentPaperWorkerContext context)
    {
        var key = proposal.Key;
        if (key.UserId == Guid.Empty || context.Worker.UserId != key.UserId || context.Worker.Id != key.WorkerId
            || context.Portfolio.UserId != key.UserId || context.Portfolio.WorkerId != key.WorkerId
            || context.Portfolio.PositionQuantity < 0m || context.Portfolio.AsOfUtc != key.AsOfUtc
            || !string.Equals(context.Worker.MarketSymbol, key.Symbol, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.Candle.Symbol, key.Symbol, StringComparison.OrdinalIgnoreCase)
            || context.Candle.Interval != key.Interval || context.Candle.OpenTimeUtc != key.OpenTimeUtc
            || context.Candle.CloseTimeUtc != key.CloseTimeUtc || context.Candle.AsOfUtc != key.AsOfUtc
            || context.Candle.ClosePrice <= 0m || context.Candle.Volume < 0m
            || context.Candle.OpenTimeUtc.Offset != TimeSpan.Zero || context.Candle.CloseTimeUtc.Offset != TimeSpan.Zero
            || context.Candle.AsOfUtc.Offset != TimeSpan.Zero || context.Candle.OpenTimeUtc >= context.Candle.CloseTimeUtc)
            throw new InvalidOperationException("Worker, portfolio, and closed-candle context must exactly match the attested decision.");
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
        private readonly decimal _openQuantity;
        public ProposalStrategy(ExperimentDecisionRecord proposal, decimal positionQuantity, decimal openQuantity) =>
            (_proposal, _positionQuantity, _openQuantity) = (proposal, positionQuantity, openQuantity);
        public Guid StrategyId => GuidFromFingerprint(Fingerprint(_proposal.Key));
        public StrategyDecision Evaluate(MarketEvent marketEvent) => new(
            Guid.NewGuid(), StrategyId, marketEvent.Symbol,
            _proposal.Proposal.Action == ExperimentProposalAction.Open ? SignalDirection.Buy : SignalDirection.Sell,
            1m, marketEvent.EventTimeUtc, _proposal.Proposal.Reason);
        public PipelineIntentDetails GetIntentDetails(MarketEvent marketEvent, StrategyDecision decision) =>
            _proposal.Proposal.Action switch
            {
                ExperimentProposalAction.Open => new(_openQuantity),
                ExperimentProposalAction.Reduce => new(Math.Min(_openQuantity, _positionQuantity), ReduceOnly: true),
                ExperimentProposalAction.Close => new(_positionQuantity, ReduceOnly: true, CloseOnly: true),
                _ => throw new InvalidOperationException("Only actionable paper experiment proposals reach intent construction.")
            };
    }
}
