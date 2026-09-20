namespace Trading.Application.Pipeline;

using Trading.Application.Execution;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Risk;

/// <summary>
/// Produces a strategy decision from a market event. Implementations are platform-authored
/// strategy templates and must never reach an exchange connector directly.
/// </summary>
public interface IPipelineStrategy
{
    Guid StrategyId { get; }

    StrategyDecision? Evaluate(MarketEvent marketEvent);
}

/// <summary>
/// Portfolio state supplied to the risk gate before exposure may be increased.
/// </summary>
public sealed class PortfolioSnapshot
{
    public PortfolioSnapshot(
        decimal currentExposure,
        decimal positionQuantity,
        decimal cashBalance,
        decimal dailyPnL,
        int openOrders,
        int openPositions,
        DateTimeOffset? lastUpdatedUtc)
    {
        if (cashBalance < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(cashBalance), "Cash balance cannot be negative.");
        }

        CurrentExposure = currentExposure;
        PositionQuantity = positionQuantity;
        CashBalance = cashBalance;
        DailyPnL = dailyPnL;
        OpenOrders = openOrders;
        OpenPositions = openPositions;
        LastUpdatedUtc = lastUpdatedUtc;
    }

    public decimal CurrentExposure { get; }

    public decimal PositionQuantity { get; }

    public decimal CashBalance { get; }

    public decimal DailyPnL { get; }

    public int OpenOrders { get; }

    public int OpenPositions { get; }

    /// <summary>Null means the snapshot age is unknown, which the risk gate treats as stale.</summary>
    public DateTimeOffset? LastUpdatedUtc { get; }
}

public sealed class TradePipelineOptions
{
    /// <summary>
    /// Live trading is disabled by default and must be switched on deliberately.
    /// A Live-mode pipeline context is rejected while this is false.
    /// </summary>
    public bool LiveTradingEnabled { get; init; }

    public decimal MaxPositionSize { get; init; } = 100m;

    public decimal MaxNotional { get; init; } = 1_000m;

    /// <summary>
    /// Mandatory immutable platform ceilings. Null means risk policy is
    /// unavailable and the pipeline fails closed.
    /// </summary>
    public RiskLimitHierarchy? PlatformRiskLimits { get; init; } =
        new(platformMaxExposure: 1_000m, platformMaxPositionSize: 100m);

    public TimeSpan MaxDataAge { get; init; } = TimeSpan.FromMinutes(5);

    public decimal OrderQuantity { get; init; } = 1m;
}

public sealed class TradePipelineResult
{
    private TradePipelineResult(
        PipelineStage reachedStage,
        bool executed,
        string? blockedReason,
        PortfolioUpdate? portfolioUpdate,
        bool requiresReconciliation)
    {
        ReachedStage = reachedStage;
        Executed = executed;
        BlockedReason = blockedReason;
        PortfolioUpdate = portfolioUpdate;
        RequiresReconciliation = requiresReconciliation;
    }

    /// <summary>The last stage the trade reached before stopping.</summary>
    public PipelineStage ReachedStage { get; }

    public bool Executed { get; }

    /// <summary>Why the trade stopped. Null only when the trade completed.</summary>
    public string? BlockedReason { get; }

    public PortfolioUpdate? PortfolioUpdate { get; }

    /// <summary>
    /// True when the exchange outcome is unknown. The order must be reconciled before any
    /// resubmission; it must never be blindly retried.
    /// </summary>
    public bool RequiresReconciliation { get; }

    internal static TradePipelineResult Blocked(PipelineStage stage, string reason) =>
        new(stage, false, reason, null, false);

    internal static TradePipelineResult NeedsReconciliation(string reason) =>
        new(PipelineStage.Reconciliation, false, reason, null, true);

    internal static TradePipelineResult Completed(PortfolioUpdate update) =>
        new(PipelineStage.AuditEvent, true, null, update, false);
}

/// <summary>
/// Enforces the mandatory trade pipeline:
/// MarketEvent -> StrategyDecision -> TradeIntent -> RiskEvaluation -> ExecutionCommand
/// -> execution adapter -> reconciliation -> PortfolioUpdate -> AuditEvent.
/// Stages cannot be skipped: each one is persisted before the next begins, and a stop at any
/// stage ends the trade.
/// </summary>
public sealed class TradePipeline
{
    private readonly IMarketEventRepository _marketEvents;
    private readonly IStrategyDecisionRepository _decisions;
    private readonly ITradeIntentRepository _intents;
    private readonly IRiskEvaluationRepository _riskEvaluations;
    private readonly IExecutionCommandRepository _commands;
    private readonly IPortfolioUpdateRepository _portfolioUpdates;
    private readonly IAuditEventWriter _auditWriter;
    private readonly RiskEngine _riskEngine;
    private readonly ITradingHaltState _haltState;
    private readonly OrderIdempotencyGuard _idempotencyGuard;
    private readonly IOrderReconciliationRepository? _reconciliations;
    private readonly TradePipelineOptions _options;

    public TradePipeline(
        IMarketEventRepository marketEvents,
        IStrategyDecisionRepository decisions,
        ITradeIntentRepository intents,
        IRiskEvaluationRepository riskEvaluations,
        IExecutionCommandRepository commands,
        IPortfolioUpdateRepository portfolioUpdates,
        IAuditEventWriter auditWriter,
        RiskEngine riskEngine,
        ITradingHaltState haltState,
        OrderIdempotencyGuard idempotencyGuard,
        TradePipelineOptions? options = null,
        IOrderReconciliationRepository? reconciliations = null)
    {
        ArgumentNullException.ThrowIfNull(marketEvents);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(intents);
        ArgumentNullException.ThrowIfNull(riskEvaluations);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(portfolioUpdates);
        ArgumentNullException.ThrowIfNull(auditWriter);
        ArgumentNullException.ThrowIfNull(riskEngine);
        ArgumentNullException.ThrowIfNull(haltState);
        ArgumentNullException.ThrowIfNull(idempotencyGuard);

        _marketEvents = marketEvents;
        _decisions = decisions;
        _intents = intents;
        _riskEvaluations = riskEvaluations;
        _commands = commands;
        _portfolioUpdates = portfolioUpdates;
        _auditWriter = auditWriter;
        _riskEngine = riskEngine;
        _haltState = haltState;
        _idempotencyGuard = idempotencyGuard;
        _reconciliations = reconciliations;
        _options = options ?? new TradePipelineOptions();
    }

    public async Task<TradePipelineResult> ProcessAsync(
        MarketEvent marketEvent,
        PipelineContext context,
        IPipelineStrategy strategy,
        PortfolioSnapshot portfolio,
        IExecutionAdapter executionAdapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketEvent);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(portfolio);
        ArgumentNullException.ThrowIfNull(executionAdapter);

        var now = DateTimeOffset.UtcNow;

        await _marketEvents.AddAsync(
            new PipelineRecord<MarketEvent>(Guid.NewGuid(), context, PipelineStage.MarketEvent, marketEvent, now),
            cancellationToken).ConfigureAwait(false);

        // Live trading is off unless explicitly enabled. Checked before any strategy runs.
        if (context.Mode == TradingMode.Live && !_options.LiveTradingEnabled)
        {
            return await BlockAsync(
                context, PipelineStage.MarketEvent,
                "Live trading is disabled. Enable it deliberately before real orders can be placed.",
                cancellationToken).ConfigureAwait(false);
        }

        // Signals are only generated from closed candles, never from a forming one.
        if (!marketEvent.IsClosed)
        {
            return await BlockAsync(
                context, PipelineStage.MarketEvent,
                "Market event is not a closed candle; signals require closed candles.",
                cancellationToken).ConfigureAwait(false);
        }

        if (marketEvent.QualityFlags.Count > 0)
        {
            return await BlockAsync(
                context, PipelineStage.MarketEvent,
                $"Market data quality issues present: {string.Join(", ", marketEvent.QualityFlags)}.",
                cancellationToken).ConfigureAwait(false);
        }

        var decision = strategy.Evaluate(marketEvent);
        if (decision is null || decision.Direction == SignalDirection.Hold)
        {
            return TradePipelineResult.Blocked(PipelineStage.StrategyDecision, "Strategy produced no actionable signal.");
        }

        await _decisions.AddAsync(
            new PipelineRecord<StrategyDecision>(Guid.NewGuid(), context, PipelineStage.StrategyDecision, decision, now),
            cancellationToken).ConfigureAwait(false);

        var direction = decision.Direction == SignalDirection.Buy ? TradeDirection.Buy : TradeDirection.Sell;
        var intent = new TradeIntent(
            Guid.NewGuid(),
            strategy.StrategyId,
            marketEvent.Symbol,
            direction,
            _options.OrderQuantity,
            marketEvent.LastPrice,
            now);

        await _intents.AddAsync(
            new PipelineRecord<TradeIntent>(Guid.NewGuid(), context, PipelineStage.TradeIntent, intent, now),
            cancellationToken).ConfigureAwait(false);

        var proposedExposure = intent.Quantity * intent.LimitPrice;

        // Halt state is read for this specific trade and covers every scope: platform emergency
        // stop, market halt, user or account halt, strategy halt, close-only and reduce-only.
        var flags = await _haltState
            .GetAsync(context, intent.Symbol, strategy.StrategyId, cancellationToken)
            .ConfigureAwait(false);

        // The deterministic client order id is derived from the intent, so replaying the same
        // intent can never create a second order. It is computed before the risk gate so the
        // duplicate check is part of the same evaluation.
        var clientOrderId = BuildClientOrderId(context, intent);

        var idempotency = _idempotencyGuard.RegisterOrCheck(
            clientOrderId, intent.Symbol, intent.Quantity, intent.LimitPrice);

        // A sell that does not shrink the position is an increase in exposure.
        var reducesExposure = intent.Direction == TradeDirection.Sell
            && portfolio.PositionQuantity > 0m;

        var riskLimits = BuildEffectiveRiskLimits(_options.PlatformRiskLimits);
        var riskResult = riskLimits is null
            ? new RiskEvaluationResult(false, "Mandatory platform risk limits are unavailable.")
            : _riskEngine.Evaluate(
                proposedExposure: proposedExposure,
                currentExposure: portfolio.CurrentExposure,
                dailyPnL: portfolio.DailyPnL,
                openOrders: portfolio.OpenOrders,
                openPositions: portfolio.OpenPositions,
                maxPositionSize: riskLimits.EffectiveMaxPositionSize,
                maxNotional: riskLimits.EffectiveMaxExposure,
                dataIsStale: false,
                accountIsHalted: flags.AccountHalted,
                strategyIsHalted: flags.StrategyHalted,
                closeOnlyMode: flags.CloseOnlyMode && !reducesExposure,
                reduceOnlyMode: flags.ReduceOnlyMode && !reducesExposure,
                duplicateOrderDetected: idempotency.IsDuplicate,
                orderIdempotencyConflict: idempotency.IsConflict,
                marketHalt: flags.MarketHalt,
                emergencyStop: flags.EmergencyStop,
                stalenessPolicy: new StalenessPolicy(_options.MaxDataAge),
                riskLimitHierarchy: riskLimits,
                lastDataUpdateUtc: portfolio.LastUpdatedUtc,
                nowUtc: now,
                proposedQuantity: intent.Quantity);

        var riskEvaluation = new RiskEvaluation(
            Guid.NewGuid(),
            intent.Id,
            riskResult.IsAllowed,
            proposedExposure,
            _options.MaxNotional,
            now,
            riskResult.Reason);

        await _riskEvaluations.AddAsync(
            new PipelineRecord<RiskEvaluation>(Guid.NewGuid(), context, PipelineStage.RiskEvaluation, riskEvaluation, now),
            cancellationToken).ConfigureAwait(false);

        if (!riskResult.IsAllowed)
        {
            return await BlockAsync(
                context, PipelineStage.RiskEvaluation,
                riskResult.Reason ?? "Blocked by the risk engine.",
                cancellationToken).ConfigureAwait(false);
        }

        // Deterministic client order id computed above, before the risk gate.
        var command = new ExecutionCommand(
            Guid.NewGuid(),
            intent.Id,
            intent.Symbol,
            intent.Direction,
            intent.Quantity,
            intent.LimitPrice,
            now,
            clientOrderId,
            isPaperOnly: context.Mode == TradingMode.Paper);

        await _commands.AddAsync(
            new PipelineRecord<ExecutionCommand>(Guid.NewGuid(), context, PipelineStage.ExecutionCommand, command, now),
            cancellationToken).ConfigureAwait(false);

        var execution = await executionAdapter.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

        // An unknown outcome must be reconciled, never blindly retried.
        if (IsUnknownOutcome(execution))
        {
            const string UnknownReason =
                "Exchange status unknown; reconciliation required before any resubmission.";

            // The record is what makes the freeze durable and visible. Without
            // it the unknown outcome would exist only as a returned value that
            // a caller could ignore.
            if (_reconciliations is not null)
            {
                await _reconciliations.AddAsync(
                    new OrderReconciliationRecord(
                        Guid.NewGuid(),
                        command.Id,
                        exchangeOrderId: null,
                        ExchangeOrderStatus.Unknown,
                        now,
                        source: $"pipeline:{context.Mode}"),
                    cancellationToken).ConfigureAwait(false);
            }

            await WriteAuditAsync(
                context, "Trade.ExecutionUnknown", intent.Id.ToString(),
                UnknownReason,
                cancellationToken).ConfigureAwait(false);

            return TradePipelineResult.NeedsReconciliation(UnknownReason);
        }

        if (!execution.Success)
        {
            return await BlockAsync(
                context, PipelineStage.Execution,
                execution.FailureReason ?? "Execution was rejected.",
                cancellationToken).ConfigureAwait(false);
        }

        var signedQuantity = command.Direction == TradeDirection.Buy
            ? execution.FilledQuantity
            : -execution.FilledQuantity;

        var cashDelta = (execution.FilledQuantity * execution.AverageFillPrice) + execution.Fees;
        var cashAfter = command.Direction == TradeDirection.Buy
            ? portfolio.CashBalance - cashDelta
            : portfolio.CashBalance + (execution.FilledQuantity * execution.AverageFillPrice) - execution.Fees;

        if (cashAfter < 0m)
        {
            return await BlockAsync(
                context, PipelineStage.Execution,
                "Execution would drive the cash balance negative.",
                cancellationToken).ConfigureAwait(false);
        }

        var portfolioUpdate = new PortfolioUpdate(
            Guid.NewGuid(),
            command.Id,
            command.Symbol,
            portfolio.PositionQuantity,
            portfolio.PositionQuantity + signedQuantity,
            portfolio.CashBalance,
            cashAfter,
            realizedPnL: 0m,
            fees: execution.Fees,
            appliedAtUtc: execution.ExecutedAtUtc);

        await _portfolioUpdates.AddAsync(
            new PipelineRecord<PortfolioUpdate>(Guid.NewGuid(), context, PipelineStage.PortfolioUpdate, portfolioUpdate, now),
            cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(
            context,
            context.Mode == TradingMode.Paper ? "Trade.PaperExecuted" : "Trade.LiveExecuted",
            command.Id.ToString(),
            $"Filled {execution.FilledQuantity} {command.Symbol} at {execution.AverageFillPrice}.",
            cancellationToken).ConfigureAwait(false);

        return TradePipelineResult.Completed(portfolioUpdate);
    }

    private static bool IsUnknownOutcome(ExecutionResult execution) =>
        string.Equals(execution.Status, "Unknown", StringComparison.OrdinalIgnoreCase);

    private static string BuildClientOrderId(PipelineContext context, TradeIntent intent) =>
        $"{(context.Mode == TradingMode.Paper ? "paper" : "live")}-{intent.Id:N}";

    private RiskLimitHierarchy? BuildEffectiveRiskLimits(RiskLimitHierarchy? platformLimits)
    {
        if (platformLimits is null)
        {
            return null;
        }

        return new RiskLimitHierarchy(
            platformLimits.PlatformMaxExposure,
            platformLimits.PlatformMaxPositionSize,
            accountMaxExposure: MoreRestrictive(platformLimits.AccountMaxExposure, _options.MaxNotional),
            accountMaxPositionSize: MoreRestrictive(platformLimits.AccountMaxPositionSize, _options.MaxPositionSize),
            userMaxExposure: platformLimits.UserMaxExposure,
            userMaxPositionSize: platformLimits.UserMaxPositionSize,
            strategyMaxExposure: platformLimits.StrategyMaxExposure,
            strategyMaxPositionSize: platformLimits.StrategyMaxPositionSize);
    }

    private static decimal? MoreRestrictive(decimal? first, decimal second) =>
        first.HasValue ? Math.Min(first.Value, second) : second;

    private async Task<TradePipelineResult> BlockAsync(
        PipelineContext context,
        PipelineStage stage,
        string reason,
        CancellationToken cancellationToken)
    {
        await WriteAuditAsync(context, $"Trade.Blocked.{stage}", context.CorrelationId, reason, cancellationToken)
            .ConfigureAwait(false);

        return TradePipelineResult.Blocked(stage, reason);
    }

    private Task WriteAuditAsync(
        PipelineContext context,
        string action,
        string targetId,
        string detail,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent(
            Guid.NewGuid(),
            context.UserId,
            action,
            nameof(TradePipeline),
            targetId,
            DateTimeOffset.UtcNow,
            null,
            detail,
            context.CorrelationId);

        return _auditWriter.WriteAsync(auditEvent, cancellationToken);
    }
}
