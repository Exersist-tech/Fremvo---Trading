using System.Globalization;
using Microsoft.Extensions.Logging;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Orders;
using Trading.Exchanges.Abstractions;
using Trading.MarketData;
using Trading.Risk;

namespace Trading.Application.Execution;

/// <summary>
/// Why a live submission ended the way it did.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is not a failure. It means the order may be working
/// at the exchange and the platform has frozen it pending reconciliation. It
/// is kept apart from <see cref="Rejected"/> for the same reason it is kept
/// apart everywhere else in this codebase.
/// </remarks>
public enum LiveTradeOutcome
{
    /// <summary>The exchange accepted the order. It is working, not filled.</summary>
    Accepted = 0,

    /// <summary>A halt, close-only or reduce-only restriction stopped the order.</summary>
    Blocked,

    /// <summary>A risk limit or proving restriction stopped the order.</summary>
    RiskBlocked,

    /// <summary>The reference price is missing or too old to size an order against.</summary>
    PriceUnavailable,

    /// <summary>The same client order id was already submitted.</summary>
    Duplicate,

    /// <summary>The exchange refused the order. No order exists.</summary>
    Rejected,

    /// <summary>
    /// The account cannot send orders: it is absent, not owned by this user,
    /// disconnected, or still in paper stage.
    /// </summary>
    AccountNotEligible,

    /// <summary>
    /// Nothing was established. The order may be live and has been frozen for
    /// reconciliation. It must not be resubmitted.
    /// </summary>
    Unknown,

    /// <summary>The request itself was not valid.</summary>
    Invalid,

    /// <summary>
    /// The exchange's current instrument catalogue could not establish that
    /// this pair is active and valid for the requested order.
    /// </summary>
    InstrumentUnavailable
}

public sealed class LiveTradeResult
{
    private LiveTradeResult(LiveTradeOutcome outcome, string message, Order? order)
    {
        Outcome = outcome;
        Message = message;
        Order = order;
    }

    public LiveTradeOutcome Outcome { get; }

    public string Message { get; }

    public Order? Order { get; }

    public bool Succeeded => Outcome == LiveTradeOutcome.Accepted;

    /// <summary>
    /// True when the platform does not know whether an order exists. Callers
    /// must not resubmit and must surface this to the user.
    /// </summary>
    public bool RequiresReconciliation => Outcome == LiveTradeOutcome.Unknown;

    internal static LiveTradeResult Accepted(Order order, string message) =>
        new(LiveTradeOutcome.Accepted, message, order);

    internal static LiveTradeResult Frozen(Order order, string message) =>
        new(LiveTradeOutcome.Unknown, message, order);

    internal static LiveTradeResult Failure(LiveTradeOutcome outcome, string message, Order? order = null) =>
        new(outcome, message, order);
}

public interface ILiveTradingService
{
    Task<LiveTradeResult> SubmitAsync(
        Guid userId,
        Guid exchangeAccountId,
        string symbol,
        OrderSide side,
        decimal quantity,
        string? clientOrderId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Submits real orders to a real exchange on behalf of a user.
/// </summary>
/// <remarks>
/// <para>
/// This is the only class in the platform that can cause a user's money to
/// move. It runs the same sequence of controls as the paper path, plus the
/// ones that only matter when the money is real, and it stops at the first
/// refusal rather than adapting the order to fit.
/// </para>
/// <para>
/// Orders are sent as <b>limit</b> orders priced at the last closed candle,
/// never as market orders. A market order accepts whatever the book offers,
/// which on a thin pair can be far from the price the user was shown. A limit
/// order that does not fill is a disappointment; a market order that fills at
/// a terrible price is a loss.
/// </para>
/// <para>
/// Acceptance is not a fill and is never reported as one. No position is
/// created here. Positions follow observed fills, so that the platform's
/// record of what the user owns comes from the exchange rather than from the
/// platform's own optimism.
/// </para>
/// </remarks>
public sealed class LiveTradingService : ILiveTradingService
{
    private static readonly Action<ILogger, Guid, Guid, string, OrderSide, decimal, Exception?> LogOrderAccepted =
        LoggerMessage.Define<Guid, Guid, string, OrderSide, decimal>(
            LogLevel.Information,
            new EventId(9101, "LiveOrderAccepted"),
            "LiveOrderAccepted {OrderId} {AccountId} {Symbol} {Side} {Quantity}");

    private static readonly Action<ILogger, Guid, Guid, string, OrderSide, decimal, Exception?> LogOrderRejected =
        LoggerMessage.Define<Guid, Guid, string, OrderSide, decimal>(
            LogLevel.Warning,
            new EventId(9102, "LiveOrderRejected"),
            "LiveOrderRejected {OrderId} {AccountId} {Symbol} {Side} {Quantity}");

    private static readonly Action<ILogger, Guid, Guid, string, OrderSide, decimal, Exception?> LogOrderUnknown =
        LoggerMessage.Define<Guid, Guid, string, OrderSide, decimal>(
            LogLevel.Warning,
            new EventId(9103, "LiveOrderUnknown"),
            "LiveOrderUnknown {OrderId} {AccountId} {Symbol} {Side} {Quantity}");

    /// <summary>
    /// The same staleness bound the paper path uses. Real money does not earn
    /// a more relaxed rule than simulated money.
    /// </summary>
    private static readonly TimeSpan MaxPriceAge = TimeSpan.FromMinutes(30);

    private const CandleInterval PricingInterval = CandleInterval.OneMinute;

    /// <summary>
    /// Stable identity for orders a person submits by hand rather than a
    /// strategy template. Distinct from the paper equivalent so halts and
    /// audit trails can address the live manual book on its own.
    /// </summary>
    public static Guid ManualLiveStrategyId { get; } = new("b7c3d2e1-4f5a-4b6c-8d9e-0f1a2b3c4d5e");

    private readonly IHistoricalCandleSource _candles;
    private readonly ITradablePairSource _pairs;
    private readonly IOrderRepository _orders;
    private readonly IExchangeAccountRepository _accounts;
    private readonly IExecutionAdapter _adapter;
    private readonly OrderReconciliationService _reconciliation;
    private readonly ITradingHaltState _haltState;
    private readonly IAuditEventWriter _auditWriter;
    private readonly RiskEngine _riskEngine;
    private readonly LiveTradingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LiveTradingService>? _logger;

    public LiveTradingService(
        IHistoricalCandleSource candles,
        ITradablePairSource pairs,
        IOrderRepository orders,
        IExchangeAccountRepository accounts,
        IExecutionAdapter adapter,
        OrderReconciliationService reconciliation,
        ITradingHaltState haltState,
        IAuditEventWriter auditWriter,
        RiskEngine riskEngine,
        LiveTradingOptions options,
        TimeProvider timeProvider,
        ILogger<LiveTradingService>? logger = null)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _pairs = pairs ?? throw new ArgumentNullException(nameof(pairs));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        _haltState = haltState ?? throw new ArgumentNullException(nameof(haltState));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _riskEngine = riskEngine ?? throw new ArgumentNullException(nameof(riskEngine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger;
    }

    public async Task<LiveTradeResult> SubmitAsync(
        Guid userId,
        Guid exchangeAccountId,
        string symbol,
        OrderSide side,
        decimal quantity,
        string? clientOrderId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (!_options.CanTradeLive(userId))
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.AccountNotEligible,
                "This account is not in the operator-approved live-trading rollout cohort.");
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.Invalid, "Symbol is required.");
        }

        if (quantity <= 0m)
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.Invalid, "Quantity must be greater than zero.");
        }

        var trimmedSymbol = symbol.Trim();
        var now = _timeProvider.GetUtcNow();
        var context = new PipelineContext(
            userId,
            TradingMode.Live,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        // Ownership is established before anything else is read. An account
        // belonging to another user is reported as ineligible rather than as
        // forbidden, so this path cannot be used to discover which account
        // identifiers exist.
        var account = await _accounts.GetByIdAsync(exchangeAccountId, cancellationToken).ConfigureAwait(false);
        if (account is null || account.UserId != userId)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.AccountNotEligible,
                "No such exchange account is available for live trading.");
        }

        if (!account.CanReachExchange)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.AccountNotEligible,
                account.Stage == TradingStage.Paper
                    ? "This account is in paper stage. Promote it before sending real orders."
                    : "This account is not currently connected to the exchange.");
        }

        var flags = await _haltState
            .GetAsync(context, trimmedSymbol, ManualLiveStrategyId, cancellationToken)
            .ConfigureAwait(false);

        if (flags.IsAnyHalt)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.Blocked,
                "Trading is halted, so no order was sent to the exchange.");
        }

        // Live manual orders do not yet carry a position to reduce, so an
        // exposure-reducing exemption cannot be proven. While close-only or
        // reduce-only is active the safe reading is to refuse.
        if (flags.CloseOnlyMode || flags.ReduceOnlyMode)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.Blocked,
                "Close-only or reduce-only mode is active, so new live orders are not accepted.");
        }

        var identifier = string.IsNullOrWhiteSpace(clientOrderId)
            // Kraken requires cl_ord_id to be a UUID v4. The durable order
            // retains this exact value, so it remains the idempotency and
            // reconciliation key end-to-end.
            ? Guid.NewGuid().ToString("D")
            : clientOrderId.Trim();

        if (identifier.Length > Order.MaximumClientOrderIdLength)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.Invalid,
                $"Client order id must be at most {Order.MaximumClientOrderIdLength} characters.");
        }

        // Checked against stored orders rather than an in-memory guard, so a
        // resubmitted identifier is refused even after a restart. The exchange
        // enforces the same id a second time, but a local refusal costs no
        // API call and no ambiguity.
        var duplicate = await _orders.FindByClientOrderIdAsync(identifier, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.Duplicate,
                "An order with this client order id already exists. It was not submitted again.");
        }

        var price = await ResolveReferencePriceAsync(trimmedSymbol, now, cancellationToken).ConfigureAwait(false);
        if (price is null)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.PriceUnavailable,
                $"No settled price within the last {MaxPriceAge.TotalMinutes:F0} minutes is available for {trimmedSymbol}. Live orders are blocked while market data is stale.");
        }

        var limitPrice = price.Value;
        var pair = await FindActivePairAsync(trimmedSymbol, cancellationToken).ConfigureAwait(false);
        if (pair is null)
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.InstrumentUnavailable,
                $"The current Kraken instrument catalogue does not confirm that {trimmedSymbol} is active for trading. No order was sent.");
        }

        var filterViolation = ValidateExchangeFilters(pair, quantity, limitPrice);
        if (filterViolation is not null)
        {
            // Never correct a user quantity or price by rounding it. A silent
            // change turns "buy this" into "buy something materially
            // different", especially when a small quantity is rounded to zero.
            return LiveTradeResult.Failure(LiveTradeOutcome.Invalid, filterViolation);
        }

        var notional = limitPrice * quantity;

        var riskLimits = _options.PlatformRiskLimits;
        var risk = riskLimits is null
            ? new RiskEvaluationResult(false, "Mandatory platform risk limits are unavailable.")
            : _riskEngine.Evaluate(
                proposedExposure: notional,
                currentExposure: 0m,
                dailyPnL: 0m,
                openOrders: 0,
                openPositions: 0,
                maxPositionSize: riskLimits.EffectiveMaxPositionSize,
                maxNotional: Math.Min(_options.MaxOrderNotional, riskLimits.EffectiveMaxExposure),
                dataIsStale: false,
                accountIsHalted: false,
                strategyIsHalted: false,
                closeOnlyMode: false,
                reduceOnlyMode: false,
                duplicateOrderDetected: false,
                orderIdempotencyConflict: false,
                marketHalt: false,
                emergencyStop: false,
                riskLimitHierarchy: riskLimits,
                provingRestriction: account.Stage == TradingStage.Proving
                    ? new ProvingRestriction(
                        trimmedSymbol,
                        notional,
                        account.ProvingNotionalCeiling,
                        _options.ProvingSymbols)
                    : null,
                proposedQuantity: quantity);

        if (!risk.IsAllowed)
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked, risk.Reason ?? "A risk limit blocked this order.");
        }

        var order = new Order(
            Guid.NewGuid(),
            userId,
            ManualLiveStrategyId,
            trimmedSymbol,
            side,
            OrderType.Limit,
            quantity,
            limitPrice,
            now,
            identifier,
            mode: TradingMode.Live,
            exchangeAccountId: account.Id);

        // Stored before the exchange is contacted. If the process dies mid
        // submission, the order exists locally with an id the exchange also
        // knows, so it can be found rather than lost.
        await _orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        var command = new ExecutionCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            trimmedSymbol,
            side == OrderSide.Buy ? TradeDirection.Buy : TradeDirection.Sell,
            quantity,
            limitPrice,
            now,
            identifier,
            isPaperOnly: false,
            exchangeAccountId: account.Id);

        var execution = await _adapter.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);

        switch (execution.Outcome)
        {
            case ExecutionOutcome.Accepted:
                order.MarkSubmitted(exchangeOrderId: null, now);
                await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
                if (_logger is not null)
                {
                    LogOrderAccepted(_logger, order.Id, account.Id, trimmedSymbol, side, quantity, null);
                }
                await AuditAsync(
                    userId,
                    "LiveOrderAccepted",
                    order,
                    $"Limit {side} of {quantity.ToString(CultureInfo.InvariantCulture)} {trimmedSymbol} at {limitPrice.ToString(CultureInfo.InvariantCulture)} accepted by the exchange. Acceptance is not a fill.",
                    context.CorrelationId,
                    cancellationToken).ConfigureAwait(false);

                return LiveTradeResult.Accepted(
                    order,
                    "The exchange accepted the order. It is working at the limit price and has not filled yet.");

            case ExecutionOutcome.Rejected:
                order.MarkRejected(execution.FailureReason ?? "The exchange refused the order.");
                await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
                if (_logger is not null)
                {
                    LogOrderRejected(_logger, order.Id, account.Id, trimmedSymbol, side, quantity, null);
                }
                await AuditAsync(
                    userId,
                    "LiveOrderRejected",
                    order,
                    execution.FailureReason ?? "The exchange refused the order.",
                    context.CorrelationId,
                    cancellationToken).ConfigureAwait(false);

                return LiveTradeResult.Failure(
                    LiveTradeOutcome.Rejected,
                    execution.FailureReason ?? "The exchange refused the order. No order exists.",
                    order);

            default:
                // Nothing was established. The order is frozen and a
                // reconciliation record is opened. It is never resubmitted
                // from here, and the caller is told plainly.
                var reason = execution.FailureReason ?? "The exchange did not answer, so the order's state is unknown.";
                await _reconciliation
                    .OpenAsync(order, "live-trading-service", reason, cancellationToken)
                    .ConfigureAwait(false);

                if (_logger is not null)
                {
                    LogOrderUnknown(_logger, order.Id, account.Id, trimmedSymbol, side, quantity, null);
                }
                await AuditAsync(
                    userId,
                    "LiveOrderUnknown",
                    order,
                    reason,
                    context.CorrelationId,
                    cancellationToken).ConfigureAwait(false);

                return LiveTradeResult.Frozen(
                    order,
                    "The exchange did not confirm this order, so it may or may not exist. It has been frozen and will be reconciled against the exchange. Do not resubmit it.");
        }
    }

    private Task AuditAsync(
        Guid userId,
        string action,
        Order order,
        string detail,
        string correlationId,
        CancellationToken cancellationToken) =>
        _auditWriter.WriteAsync(
            new AuditEvent(
                Guid.NewGuid(),
                userId,
                action,
                targetType: "LiveOrder",
                targetId: order.Id.ToString("D", CultureInfo.InvariantCulture),
                occurredAtUtc: _timeProvider.GetUtcNow(),
                before: null,
                after: detail,
                correlationId: correlationId),
            cancellationToken);

    private async Task<TradablePair?> FindActivePairAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TradablePair> pairs;

        try
        {
            pairs = await _pairs.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MarketDataSourceException)
        {
            return null;
        }

        return pairs.FirstOrDefault(pair =>
            pair.IsActive && string.Equals(pair.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ValidateExchangeFilters(
        TradablePair pair,
        decimal quantity,
        decimal limitPrice)
    {
        if (quantity < pair.MinimumQuantity)
        {
            return $"Quantity {quantity.ToString(CultureInfo.InvariantCulture)} is below Kraken's minimum of {pair.MinimumQuantity.ToString(CultureInfo.InvariantCulture)} {pair.BaseAsset} for {pair.DisplayName}.";
        }

        if (!IsMultipleOf(quantity, pair.QuantityStep))
        {
            return $"Quantity must be a whole multiple of Kraken's {pair.QuantityStep.ToString(CultureInfo.InvariantCulture)} step for {pair.DisplayName}. It was not changed.";
        }

        if (!IsMultipleOf(limitPrice, pair.PriceTick))
        {
            return $"The settled reference price is not a whole multiple of Kraken's {pair.PriceTick.ToString(CultureInfo.InvariantCulture)} tick for {pair.DisplayName}. No price was rounded and no order was sent.";
        }

        return null;
    }

    private static bool IsMultipleOf(decimal value, decimal step) =>
        value % step == 0m;

    /// <summary>
    /// The last settled price, or null when none is recent enough to act on.
    /// </summary>
    /// <remarks>
    /// Only closed candles are considered. A forming bar's close has not
    /// settled, and pricing a real order against it would send an order at a
    /// number that was never a real market price.
    /// </remarks>
    private async Task<decimal?> ResolveReferencePriceAsync(
        string symbol,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Candle> candles;

        try
        {
            candles = await _candles
                .FetchAsync(symbol, PricingInterval, now.AddHours(-6), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MarketDataSourceException)
        {
            return null;
        }

        var latestClosed = candles
            .Where(candle => candle.CanBeUsedForClosedCandleSignal)
            .OrderBy(candle => candle.CloseTimeUtc)
            .LastOrDefault();

        if (latestClosed is null || latestClosed.Close <= 0m)
        {
            return null;
        }

        return now - latestClosed.CloseTimeUtc > MaxPriceAge ? null : latestClosed.Close;
    }
}
