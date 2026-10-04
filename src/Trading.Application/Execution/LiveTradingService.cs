using System.Globalization;
using Microsoft.Extensions.Logging;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Portfolio;
using Trading.Application.Entitlements;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
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

    Task<LiveTradeResult> ClosePositionAsync(
        Guid userId,
        Guid exchangeAccountId,
        Guid positionId,
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
    private static readonly TimeSpan MaxAccountBalanceAge = TimeSpan.FromSeconds(30);

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
    private readonly IPositionRepository _positions;
    private readonly IExchangeAccountRepository _accounts;
    private readonly IExecutionAdapter _adapter;
    private readonly OrderReconciliationService _reconciliation;
    private readonly ITradingHaltState _haltState;
    private readonly IAuditEventWriter _auditWriter;
    private readonly RiskEngine _riskEngine;
    private readonly LiveTradingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILiveTradingEligibility _eligibility;
    private readonly ILiveExecutionRouteProvider _routes;
    private readonly IPortfolioQueryService _portfolio;
    private readonly ILiveAccountOpenOrderCheck _openOrders;
    private readonly ISpotExecutionAccountSource _executionAccounts;
    private readonly ILogger<LiveTradingService>? _logger;

    public LiveTradingService(
        IHistoricalCandleSource candles,
        ITradablePairSource pairs,
        IOrderRepository orders,
        IPositionRepository positions,
        IExchangeAccountRepository accounts,
        IExecutionAdapter adapter,
        OrderReconciliationService reconciliation,
        ITradingHaltState haltState,
        IAuditEventWriter auditWriter,
        RiskEngine riskEngine,
        LiveTradingOptions options,
        TimeProvider timeProvider,
        ILiveTradingEligibility eligibility,
        ILiveExecutionRouteProvider routes,
        IPortfolioQueryService portfolio,
        ILiveAccountOpenOrderCheck openOrders,
        ISpotExecutionAccountSource executionAccounts,
        ILogger<LiveTradingService>? logger = null)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _pairs = pairs ?? throw new ArgumentNullException(nameof(pairs));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        _haltState = haltState ?? throw new ArgumentNullException(nameof(haltState));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _riskEngine = riskEngine ?? throw new ArgumentNullException(nameof(riskEngine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _eligibility = eligibility ?? throw new ArgumentNullException(nameof(eligibility));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _openOrders = openOrders ?? throw new ArgumentNullException(nameof(openOrders));
        _executionAccounts = executionAccounts ?? throw new ArgumentNullException(nameof(executionAccounts));
        _logger = logger;
    }

    public Task<LiveTradeResult> SubmitAsync(
        Guid userId,
        Guid exchangeAccountId,
        string symbol,
        OrderSide side,
        decimal quantity,
        string? clientOrderId,
        CancellationToken cancellationToken = default) =>
        SubmitCoreAsync(userId, exchangeAccountId, symbol, side, quantity, clientOrderId,
            closingPositionId: null, cancellationToken);

    /// <summary>
    /// Explicitly reduces an existing, account-bound live Spot long. An expired
    /// owner entitlement does not prevent a verified safety exit.
    /// </summary>
    public async Task<LiveTradeResult> ClosePositionAsync(
        Guid userId,
        Guid exchangeAccountId,
        Guid positionId,
        decimal quantity,
        string? clientOrderId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User id is required.", nameof(userId));
        if (positionId == Guid.Empty || quantity <= 0m)
            return LiveTradeResult.Failure(LiveTradeOutcome.Invalid, "A position id and positive close quantity are required.");

        var positions = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        var matches = positions.Where(item => item.Id == positionId && item.UserId == userId
            && item.Mode == TradingMode.Live && item.ExchangeAccountId == exchangeAccountId
            && item.Direction == PositionDirection.DirectionLong
            && item.Status is (PositionStatus.Open or PositionStatus.Closing or PositionStatus.ReducedOnly)).Take(2).ToArray();
        if (matches.Length != 1 || quantity > matches[0].Quantity)
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "A verified, account-bound live Spot long of sufficient size is required to close.");

        return await SubmitCoreAsync(userId, exchangeAccountId, matches[0].Symbol, OrderSide.Sell,
            quantity, clientOrderId, positionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LiveTradeResult> SubmitCoreAsync(
        Guid userId,
        Guid exchangeAccountId,
        string symbol,
        OrderSide side,
        decimal quantity,
        string? clientOrderId,
        Guid? closingPositionId,
        CancellationToken cancellationToken)
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
        if (closingPositionId is null
            && !await _eligibility.IsEligibleAsync(userId, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false))
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.AccountNotEligible,
                "An active live-eligible owner plan is required to submit a real order.");
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.Invalid, "Symbol is required.");
        }

        if (quantity <= 0m)
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.Invalid, "Quantity must be greater than zero.");
        }
        if (side is not (OrderSide.Buy or OrderSide.Sell))
            return LiveTradeResult.Failure(LiveTradeOutcome.Invalid, "A supported Spot order side is required.");

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

        if (!_routes.HasRouteFor(account.ExchangeKind))
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.Blocked,
                "The operator has disabled the live execution route. No order was sent.");
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

        if (closingPositionId is null && (flags.CloseOnlyMode || flags.ReduceOnlyMode))
        {
            return LiveTradeResult.Failure(
                LiveTradeOutcome.Blocked,
                "Close-only or reduce-only mode is active, so new live orders are not accepted.");
        }

        if (side == OrderSide.Sell)
        {
            var open = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);
            var pairPositions = open.Where(position =>
                position.Mode == TradingMode.Live
                && string.Equals(position.Symbol, trimmedSymbol, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var owned = pairPositions.Where(position => position.ExchangeAccountId == exchangeAccountId).ToArray();
            if (pairPositions.Any(position => position.ExchangeAccountId is null)
                || owned.Length != 1
                || owned[0].Direction != PositionDirection.DirectionLong
                || (closingPositionId is not null && owned[0].Id != closingPositionId)
                || owned[0].Status is not (PositionStatus.Open or PositionStatus.Closing or PositionStatus.ReducedOnly)
                || quantity > owned[0].Quantity)
                return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                    "A live Spot sell requires a verified long position of sufficient size on this exact exchange account.");

            var existingOrders = await _orders.ListAsync(userId, cancellationToken).ConfigureAwait(false);
            if (existingOrders.Any(order => order.Mode == TradingMode.Live
                && order.ExchangeAccountId == exchangeAccountId
                && string.Equals(order.Symbol, trimmedSymbol, StringComparison.OrdinalIgnoreCase)
                && (order.State is OrderState.New or OrderState.Accepted or OrderState.PartiallyFilled
                    || order.RequiresReconciliation)))
                return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                    "Reconcile the account's working or unknown live order before selling this position.");
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

        decimal notional;
        try
        {
            notional = limitPrice * quantity;
        }
        catch (OverflowException)
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "The proposed live order notional cannot be represented safely.");
        }
        if (notional > _options.MaxOrderNotional)
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "The proposed live order exceeds the platform per-order notional ceiling.");

        var requiredAsset = side == OrderSide.Buy ? pair.QuoteAsset : pair.BaseAsset;
        var requiredAmount = side == OrderSide.Buy ? notional : quantity;
        var funds = await _portfolio.ReadAccountAsync(userId, account.Id, cancellationToken).ConfigureAwait(false);
        if (funds is null || funds.AccountId != account.Id || funds.Error is not null
            || !HasUnreservedCash(funds, requiredAsset, requiredAmount, _timeProvider.GetUtcNow()))
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "Current unreserved Spot funds could not be verified; no live order was sent.");

        var openPositions = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        var positionRevisions = openPositions.Where(position => position.Mode == TradingMode.Live)
            .Select(position => (position.Id, position.Version)).ToHashSet();
        if (openPositions.Any(position => position.Mode == TradingMode.Live
                && position.ExchangeAccountId is null))
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "Legacy live positions without exchange-account identity require reconciliation before new orders.");
        var accountPositions = openPositions.Where(position =>
            position.Mode == TradingMode.Live && position.ExchangeAccountId == account.Id).ToArray();
        if (closingPositionId is not null
            && (side != OrderSide.Sell || accountPositions.Length != 1
                || accountPositions[0].Id != closingPositionId
                || accountPositions[0].Direction != PositionDirection.DirectionLong
                || accountPositions[0].Status is not (PositionStatus.Open or PositionStatus.Closing or PositionStatus.ReducedOnly)
                || quantity > accountPositions[0].Quantity))
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "The live Spot close is no longer bound to the selected long position.");
        if (accountPositions.Any(position => position.Direction != PositionDirection.DirectionLong
                || !string.Equals(position.Symbol, trimmedSymbol, StringComparison.OrdinalIgnoreCase)))
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "Existing live exposure in another instrument or direction cannot be priced in this order's quote currency.");
        if (!HasReconciledBaseHoldings(funds, pair.BaseAsset, pair.QuoteAsset, accountPositions))
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "The exchange account contains unverified holdings or its base-asset balance does not match recorded live positions.");

        var accountOrders = await _orders.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        if (accountOrders.Any(order => order.Mode == TradingMode.Live
                && order.ExchangeAccountId == account.Id
                && (!order.IsTerminal || order.RequiresReconciliation)))
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "The account has an unresolved live order. Reconcile it before another order is submitted.");

        if (await _openOrders.ReadAsync(userId, account.Id, account.ExchangeKind, cancellationToken)
            .ConfigureAwait(false) != SpotOpenOrderState.Empty)
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "The selected account's exchange open orders could not be verified empty. Reconcile external orders before submitting.");

        decimal currentExposure;
        decimal proposedPositionQuantity;
        try
        {
            currentExposure = accountPositions.Sum(position => position.Quantity * limitPrice);
            proposedPositionQuantity = side == OrderSide.Buy
                ? accountPositions.Sum(position => position.Quantity) + quantity
                : quantity;
        }
        catch (OverflowException)
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "The recorded live exposure or proposed position quantity cannot be represented safely.");
        }

        var riskLimits = _options.PlatformRiskLimits;
        var risk = riskLimits is null
            ? new RiskEvaluationResult(false, "Mandatory platform risk limits are unavailable.")
            : _riskEngine.Evaluate(
                proposedExposure: notional,
                currentExposure: currentExposure,
                dailyPnL: 0m,
                openOrders: 0,
                openPositions: accountPositions.Length,
                maxPositionSize: riskLimits.EffectiveMaxPositionSize,
                maxNotional: riskLimits.EffectiveMaxExposure,
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
                proposedQuantity: proposedPositionQuantity,
                exposureIsIncreasing: side == OrderSide.Buy);

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
        try
        {
            await _orders.AddAsync(order, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkingLiveOrderConflictException)
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "Another live order reserved this account first. Reconcile it before submitting again.");
        }
        catch (DuplicateClientOrderIdException)
        {
            return LiveTradeResult.Failure(LiveTradeOutcome.Duplicate,
                "An order with this client order id already exists. It was not submitted again.");
        }

        if (closingPositionId is null
            && !await _eligibility.IsEligibleAsync(userId, _timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false))
        {
            order.MarkRejected("The live-eligible owner plan expired or was revoked before submission.");
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            await AuditAsync(userId, "LiveOrderRejected", order,
                "The live-eligible owner plan changed before exchange submission.",
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            return LiveTradeResult.Failure(LiveTradeOutcome.AccountNotEligible,
                "An active live-eligible owner plan is required to send this order.", order);
        }
        if (!_routes.HasRouteFor(account.ExchangeKind))
        {
            order.MarkRejected("The live execution route was withdrawn before submission.");
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            await AuditAsync(userId, "LiveOrderRejected", order,
                "The operator withdrew the live execution route before exchange submission.",
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "The operator has disabled the live execution route. No order was sent.", order);
        }
        var currentAccount = await _accounts.GetByIdAsync(account.Id, cancellationToken).ConfigureAwait(false);
        if (currentAccount is null || currentAccount.UserId != userId
            || currentAccount.ExchangeKind != account.ExchangeKind || !currentAccount.CanReachExchange)
        {
            order.MarkRejected("Live account or trading halt state changed before submission.");
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            await AuditAsync(userId, "LiveOrderRejected", order,
                "Live account or trading halt state changed before exchange submission.",
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "The selected live account or trading mode no longer permits this order.", order);
        }
        var freshFunds = await _portfolio.ReadAccountAsync(userId, account.Id, cancellationToken).ConfigureAwait(false);
        var currentPositions = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        var currentRevisions = currentPositions.Where(position => position.Mode == TradingMode.Live)
            .Select(position => (position.Id, position.Version)).ToHashSet();
        if (freshFunds is null || freshFunds.AccountId != account.Id || freshFunds.Error is not null
            || !HasUnreservedCash(freshFunds, requiredAsset, requiredAmount, _timeProvider.GetUtcNow())
            || !positionRevisions.SetEquals(currentRevisions)
            || (closingPositionId is not null && !currentPositions.Any(position =>
                position.Id == closingPositionId && position.UserId == userId
                && position.Mode == TradingMode.Live && position.ExchangeAccountId == account.Id
                && position.Direction == PositionDirection.DirectionLong
                && position.Status is (PositionStatus.Open or PositionStatus.Closing or PositionStatus.ReducedOnly)
                && position.Quantity >= quantity))
            || !HasReconciledBaseHoldings(freshFunds, pair.BaseAsset, pair.QuoteAsset, accountPositions))
        {
            order.MarkRejected("Live account funds or position evidence changed before submission.");
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            await AuditAsync(userId, "LiveOrderRejected", order,
                "Live account funds or position evidence changed before exchange submission.",
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            return LiveTradeResult.Failure(LiveTradeOutcome.RiskBlocked,
                "Current unreserved Spot funds and unchanged positions are required before submitting an order.", order);
        }

        if (await _openOrders.ReadAsync(userId, account.Id, account.ExchangeKind, cancellationToken)
            .ConfigureAwait(false) != SpotOpenOrderState.Empty)
        {
            order.MarkRejected("Exchange open orders could not be verified empty before submission.");
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            await AuditAsync(userId, "LiveOrderRejected", order,
                "Exchange open-order evidence changed or became unavailable before submission.",
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "The selected account's exchange open orders could not be verified empty. No order was sent.", order);
        }

        var executionAccount = await _executionAccounts.ResolveAsync(account.Id, cancellationToken)
            .ConfigureAwait(false);
        if (executionAccount is null || executionAccount.AccountId != account.Id
            || executionAccount.UserId != userId || executionAccount.Exchange != account.ExchangeKind
            || executionAccount.Stage != currentAccount.Stage || executionAccount.Stage == TradingStage.Paper)
        {
            order.MarkRejected("Selected account credentials are no longer eligible for live execution.");
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            await AuditAsync(userId, "LiveOrderRejected", order,
                "Selected account credentials could not be reverified before submission.",
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            return LiveTradeResult.Failure(LiveTradeOutcome.AccountNotEligible,
                "The selected account credentials could not be verified. No order was sent.", order);
        }
        var currentFlags = await _haltState.GetAsync(context, trimmedSymbol, ManualLiveStrategyId, cancellationToken)
            .ConfigureAwait(false);
        if (currentFlags.IsAnyHalt
            || (closingPositionId is null && (currentFlags.CloseOnlyMode || currentFlags.ReduceOnlyMode)))
        {
            order.MarkRejected("Trading halt state changed before submission.");
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            await AuditAsync(userId, "LiveOrderRejected", order,
                "Trading halt state changed before exchange submission.",
                context.CorrelationId, cancellationToken).ConfigureAwait(false);
            return LiveTradeResult.Failure(LiveTradeOutcome.Blocked,
                "Trading is halted or restricted. No order was sent.", order);
        }

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

    private static bool HasUnreservedCash(
        PortfolioAccountReading reading, string venueAsset, decimal requiredAmount, DateTimeOffset nowUtc)
    {
        if (reading.RetrievedAtUtc is not { } observedAt || observedAt.Offset != TimeSpan.Zero
            || observedAt > nowUtc || nowUtc - observedAt > MaxAccountBalanceAge)
            return false;

        var balances = reading.Balances.Where(balance =>
            string.Equals(balance.VenueAsset, venueAsset, StringComparison.OrdinalIgnoreCase)).ToArray();
        return balances.Length == 1
            && balances[0].Available is { } available
            && balances[0].Held is { } held
            && held >= 0m && balances[0].Total >= held
            && available >= requiredAmount
            && balances[0].Total - held >= requiredAmount;
    }

    private static bool HasReconciledBaseHoldings(
        PortfolioAccountReading reading, string venueBaseAsset, string venueQuoteAsset,
        IReadOnlyCollection<Position> positions)
    {
        decimal recordedQuantity;
        try
        {
            recordedQuantity = positions.Sum(position => position.Quantity);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (reading.Balances.Any(balance =>
                balance.Total < 0m
                || (!string.Equals(balance.VenueAsset, venueBaseAsset, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(balance.VenueAsset, venueQuoteAsset, StringComparison.OrdinalIgnoreCase)
                    && balance.Total != 0m)))
            return false;

        var balances = reading.Balances.Where(balance =>
            string.Equals(balance.VenueAsset, venueBaseAsset, StringComparison.OrdinalIgnoreCase)).ToArray();
        return balances.Length == 0
            ? recordedQuantity == 0m
            : balances.Length == 1 && balances[0].Total == recordedQuantity;
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

        return new StalenessPolicy(MaxPriceAge).IsStale(latestClosed.CloseTimeUtc, now)
            ? null
            : latestClosed.Close;
    }
}
