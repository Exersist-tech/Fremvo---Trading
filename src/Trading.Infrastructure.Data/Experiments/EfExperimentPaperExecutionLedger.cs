using System.Data;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;

namespace Trading.Infrastructure.Data.Experiments;

public enum PaperExecutionRecoveryResult { Reconciled, NoUnresolvedClaim, HostNotStopped, EvidenceIncomplete }

/// <summary>
/// Durable claim store for the experiment-to-paper boundary. The unique decision/candle key is
/// inserted before pipeline execution, making a restart fail closed instead of submitting again.
/// </summary>
public sealed class EfExperimentPaperExecutionLedger : IExperimentPaperExecutionLedger
{
    private const string RecoveryAction = "PaperExecution.FilledReconciled";
    private const string RecoveryTarget = "ExperimentPaperExecution";
    private readonly TradingDbContext _context;
    public EfExperimentPaperExecutionLedger(TradingDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<(ExperimentPaperExecutionClaimResult Result, ExperimentPaperExecutionAssociation? Association)> ClaimAsync(
        Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (userId == Guid.Empty || association.DecisionKey.UserId != userId)
            throw new InvalidOperationException("Paper execution associations must be claimed by their owner.");
        if (association.Status != ExperimentPaperExecutionStatus.Claimed)
            throw new ArgumentException("A new paper execution association must be Claimed.", nameof(association));

        var transaction = await _context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await _context.ExperimentPaperExecutionAssociations
            .FromSqlInterpolated($"""
                SELECT * FROM [ExperimentPaperExecutionAssociations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [UserId] = {userId} AND [WorkerId] = {association.DecisionKey.WorkerId}
                """)
            .AsNoTracking()
            .AnyAsync(cancellationToken).ConfigureAwait(false);

        var existing = await FindAsync(association.DecisionKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return (string.Equals(existing.CorrelationId, association.CorrelationId, StringComparison.Ordinal)
                ? ExperimentPaperExecutionClaimResult.Existing
                : ExperimentPaperExecutionClaimResult.Conflict, ToDomain(existing));

        if (await HasUnresolvedAsync(userId, association.DecisionKey.WorkerId, cancellationToken).ConfigureAwait(false))
            return (ExperimentPaperExecutionClaimResult.Conflict, null);

        var entity = ToPersisted(association);
        _context.ExperimentPaperExecutionAssociations.Add(entity);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (ExperimentPaperExecutionClaimResult.Claimed, association);
    }

    public async Task CompleteAsync(Guid userId, ExperimentPaperExecutionAssociation association, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(association);
        if (association.DecisionKey.UserId != userId)
            throw new InvalidOperationException("Only the owner may complete a paper execution association.");
        if (association.Status is not (ExperimentPaperExecutionStatus.Completed
            or ExperimentPaperExecutionStatus.Blocked or ExperimentPaperExecutionStatus.Unknown))
            throw new ArgumentException("A paper execution claim needs a valid terminal outcome.", nameof(association));
        var key = association.DecisionKey;
        var changed = await _context.ExperimentPaperExecutionAssociations
            .Where(x => x.UserId == userId && x.WorkerId == key.WorkerId
                && x.GroupConfigurationVersion == key.GroupConfigurationVersion && x.Group == (int)key.Group
                && x.StrategyId == key.StrategyId && x.StrategyVersion == key.StrategyVersion
                && x.StrategyFingerprint == key.StrategyFingerprint && x.Symbol == key.Symbol
                && x.Interval == (int)key.Interval && x.OpenTimeUtc == key.OpenTimeUtc
                && x.CloseTimeUtc == key.CloseTimeUtc && x.AsOfUtc == key.AsOfUtc
                && x.CorrelationId == association.CorrelationId
                && x.Status == (int)ExperimentPaperExecutionStatus.Claimed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, (int)association.Status)
                .SetProperty(x => x.ExecutionCommandId, association.ExecutionCommandId)
                .SetProperty(x => x.Detail, association.Detail), cancellationToken).ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException("Only the owner of an unresolved Claimed execution may record its first outcome.");
    }

    public Task<bool> HasUnresolvedAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        var owner = userId.ToString("D");
        var worker = workerId.ToString("D");
        return _context.ExperimentPaperExecutionAssociations.AsNoTracking()
            .AnyAsync(value => value.UserId == userId && value.WorkerId == workerId
                && (value.Status == (int)ExperimentPaperExecutionStatus.Claimed
                    || value.Status == (int)ExperimentPaperExecutionStatus.Unknown)
                && !_context.AuditEvents.Any(audit => audit.Action == RecoveryAction
                    && audit.TargetType == RecoveryTarget && audit.TargetId == worker
                    && audit.Before == owner && audit.CorrelationId == value.CorrelationId),
                cancellationToken);
    }

    public async Task<ExperimentPaperExecutionAssociation?> GetUnresolvedAsync(
        Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || workerId == Guid.Empty)
            throw new ArgumentException("An owner and worker are required.");
        var owner = userId.ToString("D");
        var worker = workerId.ToString("D");
        var match = await _context.ExperimentPaperExecutionAssociations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserId == userId && value.WorkerId == workerId
                && (value.Status == (int)ExperimentPaperExecutionStatus.Claimed
                    || value.Status == (int)ExperimentPaperExecutionStatus.Unknown)
                && !_context.AuditEvents.Any(audit => audit.Action == RecoveryAction
                    && audit.TargetType == RecoveryTarget && audit.TargetId == worker
                    && audit.Before == owner && audit.CorrelationId == value.CorrelationId),
                cancellationToken).ConfigureAwait(false);
        return match is null ? null : ToDomain(match);
    }

    /// <summary>
    /// Records an immutable filled resolution only when every durable stage agrees. The experiment
    /// host must be stopped by the operator before calling this; a stale heartbeat is required but
    /// cannot, on its own, prove that an in-flight process has stopped.
    /// </summary>
    public async Task<PaperExecutionRecoveryResult> ReconcileFilledAsync(
        Guid userId, Guid workerId, Guid administratorId, string correlationId,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || workerId == Guid.Empty || administratorId == Guid.Empty
            || string.IsNullOrWhiteSpace(correlationId) || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Recovery requires an owner, worker, administrator, correlation and UTC time.");

        var transaction = await _context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var claim = await _context.ExperimentPaperExecutionAssociations
            .FromSqlInterpolated($"""
                SELECT * FROM [ExperimentPaperExecutionAssociations] WITH (UPDLOCK, HOLDLOCK)
                WHERE [UserId] = {userId} AND [WorkerId] = {workerId}
                AND [Status] IN ({(int)ExperimentPaperExecutionStatus.Claimed}, {(int)ExperimentPaperExecutionStatus.Unknown})
                """)
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (claim is null || !string.Equals(claim.CorrelationId, correlationId, StringComparison.Ordinal)
            || !claim.CorrelationId.StartsWith("paper-", StringComparison.Ordinal)
            || !await HasUnresolvedAsync(userId, workerId, cancellationToken).ConfigureAwait(false))
            return PaperExecutionRecoveryResult.NoUnresolvedClaim;

        var lastHeartbeat = await new EfPaperHostHeartbeatRepository(_context)
            .LastAsync(EfPaperHostHeartbeatRepository.Experiments, cancellationToken).ConfigureAwait(false);
        if (lastHeartbeat is null || lastHeartbeat > now || now - lastHeartbeat <= TimeSpan.FromMinutes(2))
            return PaperExecutionRecoveryResult.HostNotStopped;

        var commands = await new EfPaperExecutionCommands(_context)
            .ListByCorrelationAsync(userId, correlationId, cancellationToken).ConfigureAwait(false);
        var results = await new EfPaperExecutionResults(_context)
            .ListByCorrelationAsync(userId, correlationId, cancellationToken).ConfigureAwait(false);
        var updates = await new EfPaperPortfolioUpdates(_context)
            .ListByCorrelationAsync(userId, correlationId, cancellationToken).ConfigureAwait(false);
        var tradeAudits = await _context.AuditEvents.AsNoTracking()
            .Where(row => row.ActorUserId == userId && row.CorrelationId == correlationId
                && row.TargetType == "TradePipeline" && row.Action.StartsWith("Trade."))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var paperWorker = await new EfExperimentWorkerRepository(_context)
            .GetAsync(userId, workerId, cancellationToken).ConfigureAwait(false);
        if (commands.Count != 1 || results.Count != 1 || updates.Count != 1
            || tradeAudits.Count != 1 || paperWorker is null)
            return PaperExecutionRecoveryResult.EvidenceIncomplete;

        var command = commands.Single().Payload;
        var execution = results.Single().Payload;
        var update = updates.Single().Payload;
        var audit = tradeAudits.Single();
        var ledger = paperWorker.Ledger.Where(entry => entry.Id == command.Id).ToArray();
        if (claim.ExecutionCommandId is Guid claimedId && claimedId != command.Id
            || command.IsPaperOnly != true || command.ExchangeAccountId is not null
            || !command.Symbol.Equals(claim.Symbol, StringComparison.Ordinal)
            || !command.Symbol.Equals(paperWorker.MarketSymbol, StringComparison.Ordinal)
            || execution.Outcome != ExecutionOutcome.Filled
            || execution.ExecutionCommandId != command.Id
            || execution.FilledQuantity != command.Quantity
            || execution.AverageFillPrice != command.Price
            || execution.Fees < 0m || execution.ExecutedAtUtc.Offset != TimeSpan.Zero
            || update.ExecutionCommandId != command.Id
            || !update.Symbol.Equals(command.Symbol, StringComparison.Ordinal)
            || ledger.Length != 1 || paperWorker.Ledger.Last().Id != command.Id
            || audit.Action != "Trade.PaperExecuted"
            || audit.TargetId != command.Id.ToString("D"))
            return PaperExecutionRecoveryResult.EvidenceIncomplete;

        var fill = ledger[0];
        var signedQuantity = command.Direction == TradeDirection.Buy
            ? execution.FilledQuantity : -execution.FilledQuantity;
        var cashChange = command.Direction == TradeDirection.Buy
            ? -(execution.FilledQuantity * execution.AverageFillPrice + execution.Fees)
            : execution.FilledQuantity * execution.AverageFillPrice - execution.Fees;
        if (fill.WorkerId != workerId || !fill.Symbol.Equals(command.Symbol, StringComparison.Ordinal)
            || fill.Direction != (command.Direction == TradeDirection.Buy ? "buy" : "sell")
            || fill.Quantity != execution.FilledQuantity || fill.ExecutionPrice != execution.AverageFillPrice
            || fill.Fee != execution.Fees || fill.OccurredAtUtc != execution.ExecutedAtUtc
            || update.PositionDelta != signedQuantity || update.Fees != execution.Fees
            || update.CashBalanceAfter - update.CashBalanceBefore != cashChange
            || update.PositionQuantityAfter != paperWorker.PositionQuantity
            || update.CashBalanceAfter != paperWorker.CashBalance)
            return PaperExecutionRecoveryResult.EvidenceIncomplete;

        if (paperWorker.PositionQuantity > 0m)
        {
            var opened = PaperProtectivePositionEvidence.OpenedAt(paperWorker);
            var plans = await new EfExperimentPaperPlanEvidenceRepository(_context)
                .ListAsync(userId, workerId, cancellationToken).ConfigureAwait(false);
            if (paperWorker.Status is not (ExperimentWorkerStatus.Running or ExperimentWorkerStatus.Paused or ExperimentWorkerStatus.Failed)
                || opened is null || PaperProtectivePositionEvidence.FindPlan(paperWorker, opened.Value, plans) is null)
                return PaperExecutionRecoveryResult.EvidenceIncomplete;
        }

        _context.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(), administratorId, RecoveryAction, RecoveryTarget, workerId.ToString("D"),
            now, userId.ToString("D"), command.Id.ToString("D"), correlationId));
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PaperExecutionRecoveryResult.Reconciled;
    }

    private Task<PersistedExperimentPaperExecutionAssociation?> FindAsync(ExperimentDecisionKey key, CancellationToken token) =>
        _context.ExperimentPaperExecutionAssociations.SingleOrDefaultAsync(x => x.UserId == key.UserId && x.WorkerId == key.WorkerId
            && x.GroupConfigurationVersion == key.GroupConfigurationVersion && x.Group == (int)key.Group
            && x.StrategyId == key.StrategyId && x.StrategyVersion == key.StrategyVersion && x.StrategyFingerprint == key.StrategyFingerprint
            && x.Symbol == key.Symbol && x.Interval == (int)key.Interval && x.OpenTimeUtc == key.OpenTimeUtc
            && x.CloseTimeUtc == key.CloseTimeUtc && x.AsOfUtc == key.AsOfUtc, token);

    private static PersistedExperimentPaperExecutionAssociation ToPersisted(ExperimentPaperExecutionAssociation value) => new()
    {
        UserId = value.DecisionKey.UserId, WorkerId = value.DecisionKey.WorkerId, GroupConfigurationVersion = value.DecisionKey.GroupConfigurationVersion,
        Group = (int)value.DecisionKey.Group, StrategyId = value.DecisionKey.StrategyId, StrategyVersion = value.DecisionKey.StrategyVersion,
        StrategyFingerprint = value.DecisionKey.StrategyFingerprint, Symbol = value.DecisionKey.Symbol, Interval = (int)value.DecisionKey.Interval,
        OpenTimeUtc = value.DecisionKey.OpenTimeUtc, CloseTimeUtc = value.DecisionKey.CloseTimeUtc, AsOfUtc = value.DecisionKey.AsOfUtc,
        CorrelationId = value.CorrelationId, Status = (int)value.Status, ExecutionCommandId = value.ExecutionCommandId, Detail = value.Detail
    };

    private static ExperimentPaperExecutionAssociation ToDomain(PersistedExperimentPaperExecutionAssociation value) => new(
        new(value.UserId, value.WorkerId, value.GroupConfigurationVersion, (ExperimentResearchGroup)value.Group,
            value.StrategyId, value.StrategyVersion, value.StrategyFingerprint, value.Symbol, (CandleInterval)value.Interval,
            value.OpenTimeUtc, value.CloseTimeUtc, value.AsOfUtc),
        value.CorrelationId, (ExperimentPaperExecutionStatus)value.Status, value.ExecutionCommandId, value.Detail);
}
