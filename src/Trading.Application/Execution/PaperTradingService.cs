using System.Globalization;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.MarketData;
using Trading.Risk;

namespace Trading.Application.Execution;

public enum PaperTradeOutcome
{
    /// <summary>The simulated order filled and the position was updated.</summary>
    Filled = 0,

    /// <summary>A halt, close-only or reduce-only restriction stopped the order.</summary>
    Blocked,

    /// <summary>No usable closed candle was available to price the fill.</summary>
    PriceUnavailable,

    /// <summary>The most recent closed candle is too old to price a fill against.</summary>
    PriceStale,

    /// <summary>The same client order id was already submitted.</summary>
    Duplicate,

    /// <summary>The order was refused for a reason specific to the request.</summary>
    Rejected,
}

public sealed class PaperTradeResult
{
    private PaperTradeResult(
        PaperTradeOutcome outcome,
        string message,
        Order? order,
        Position? position)
    {
        Outcome = outcome;
        Message = message;
        Order = order;
        Position = position;
    }

    public PaperTradeOutcome Outcome { get; }

    public string Message { get; }

    public Order? Order { get; }

    public Position? Position { get; }

    public bool Succeeded => Outcome == PaperTradeOutcome.Filled;

    internal static PaperTradeResult Filled(Order order, Position position, string message) =>
        new(PaperTradeOutcome.Filled, message, order, position);

    internal static PaperTradeResult Failure(PaperTradeOutcome outcome, string message) =>
        new(outcome, message, null, null);
}

public interface IPaperTradingService
{
    Task<PaperTradeResult> SubmitAsync(
        Guid userId,
        string symbol,
        OrderSide side,
        decimal quantity,
        string? clientOrderId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Simulated trading against real market prices.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here can reach an exchange. There is no credential, no order
/// gateway and no execution adapter in this class; the only venue contact is a
/// read of public price history. Its pipeline context is fixed to
/// <see cref="TradingMode.Paper"/> and there is no parameter that changes it,
/// so this service cannot be repurposed into a live path by configuration.
/// </para>
/// <para>
/// Fills are priced from the most recent <em>closed</em> candle. A bar that is
/// still forming is excluded: its close is not final, so filling against it
/// would price a trade at a number that has not settled and would flatter
/// results in a way live trading could never reproduce.
/// </para>
/// </remarks>
public sealed class PaperTradingService : IPaperTradingService
{
    /// <summary>
    /// Paper trading is not an exemption from the staleness rule. A simulated
    /// fill against a price that stopped updating teaches the wrong thing about
    /// a strategy, so the same block applies here as in live trading.
    /// </summary>
    private static readonly TimeSpan MaxPriceAge = TimeSpan.FromMinutes(30);

    private const CandleInterval PricingInterval = CandleInterval.OneMinute;

    private readonly IHistoricalCandleSource _candles;
    private readonly IOrderRepository _orders;
    private readonly IPositionRepository _positions;
    private readonly ITradingHaltState _haltState;
    private readonly IAuditEventWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public PaperTradingService(
        IHistoricalCandleSource candles,
        IOrderRepository orders,
        IPositionRepository positions,
        ITradingHaltState haltState,
        IAuditEventWriter auditWriter,
        TimeProvider timeProvider)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _haltState = haltState ?? throw new ArgumentNullException(nameof(haltState));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<PaperTradeResult> SubmitAsync(
        Guid userId,
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

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity <= 0m)
        {
            return PaperTradeResult.Failure(PaperTradeOutcome.Rejected, "Quantity must be greater than zero.");
        }

        var trimmedSymbol = symbol.Trim();
        var now = _timeProvider.GetUtcNow();

        // A fixed strategy identity stands in for the manual operator. Paper
        // orders are still attributed, because an unattributed order cannot be
        // audited or halted by strategy scope.
        var strategyId = ManualPaperStrategyId;

        var context = new PipelineContext(userId, TradingMode.Paper, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        var flags = await _haltState
            .GetAsync(context, trimmedSymbol, strategyId, cancellationToken)
            .ConfigureAwait(false);

        var existing = await FindOpenPositionAsync(userId, trimmedSymbol, cancellationToken).ConfigureAwait(false);
        var isReducing = existing is not null && IsOpposite(existing.Direction, side);

        if (flags.IsAnyHalt)
        {
            return PaperTradeResult.Failure(
                PaperTradeOutcome.Blocked,
                "Trading is halted, so no order was submitted. Clear the halt before trading.");
        }

        // Close-only and reduce-only permit removing exposure but never adding
        // it, so an order is judged by what it does to the position rather than
        // by its side alone.
        if ((flags.CloseOnlyMode || flags.ReduceOnlyMode) && !isReducing)
        {
            return PaperTradeResult.Failure(
                PaperTradeOutcome.Blocked,
                "Only exposure-reducing orders are accepted while close-only or reduce-only mode is active.");
        }

        // A caller-supplied identifier is the idempotency key. When none is
        // given the submission is a fresh intent, so the generated identifier
        // carries its own uniqueness rather than relying on the clock: two
        // orders placed within the same millisecond are distinct intents and
        // must not collide into a false duplicate.
        var identifier = string.IsNullOrWhiteSpace(clientOrderId)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"paper-{userId:N}-{now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}")
            : clientOrderId.Trim();

        // Idempotency is checked against stored orders rather than an in-memory
        // guard, so a resubmitted identifier is refused even after a restart.
        var duplicate = await _orders.FindByClientOrderIdAsync(identifier, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            return PaperTradeResult.Failure(
                PaperTradeOutcome.Duplicate,
                "An order with this client order id already exists. It was not submitted again.");
        }

        var price = await ResolveFillPriceAsync(trimmedSymbol, now, cancellationToken).ConfigureAwait(false);
        if (price.Outcome != PaperTradeOutcome.Filled)
        {
            return PaperTradeResult.Failure(price.Outcome, price.Message);
        }

        var fillPrice = price.Price;

        if (existing is not null && !isReducing)
        {
            // Averaging into a position would need a cost basis the position
            // model does not carry. Inventing one would misstate entry price
            // and therefore every profit and loss figure derived from it.
            return PaperTradeResult.Failure(
                PaperTradeOutcome.Rejected,
                "A position is already open on this pair. Increasing an existing position is not supported yet, because the entry price would have to be averaged and the position model does not yet carry a cost basis.");
        }

        if (existing is not null && quantity > existing.Quantity)
        {
            return PaperTradeResult.Failure(
                PaperTradeOutcome.Rejected,
                "Reducing by more than the open quantity would open a position in the opposite direction. Close the position first.");
        }

        var order = new Order(
            Guid.NewGuid(),
            userId,
            strategyId,
            trimmedSymbol,
            side,
            OrderType.Market,
            quantity,
            fillPrice,
            now,
            identifier,
            reduceOnly: isReducing);

        await _orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        // The simulated venue accepts and fills immediately. The order still
        // moves through its real states so the stored history matches the shape
        // a live order would produce.
        order.MarkSubmitted(exchangeOrderId: null, now);
        order.MarkPartiallyFilled(quantity, now);
        await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

        Position position;

        if (existing is null)
        {
            position = new Position(
                Guid.NewGuid(),
                userId,
                strategyId,
                trimmedSymbol,
                side == OrderSide.Buy ? PositionDirection.DirectionLong : PositionDirection.DirectionShort,
                quantity,
                fillPrice,
                fillPrice,
                now);

            await _positions.AddAsync(position, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            existing.Reduce(quantity);
            existing.UpdateMarkPrice(fillPrice);
            await _positions.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
            position = existing;
        }

        await _auditWriter.WriteAsync(
            new AuditEvent(
                Guid.NewGuid(),
                userId,
                "PaperOrderFilled",
                targetType: "PaperOrder",
                targetId: order.Id.ToString("D", CultureInfo.InvariantCulture),
                occurredAtUtc: now,
                before: null,
                after: $"Simulated {side} of {quantity.ToString(CultureInfo.InvariantCulture)} {trimmedSymbol} at {fillPrice.ToString(CultureInfo.InvariantCulture)}. No exchange was contacted and no real funds moved.",
                correlationId: context.CorrelationId),
            cancellationToken).ConfigureAwait(false);

        return PaperTradeResult.Filled(
            order,
            position,
            $"Simulated fill at {fillPrice.ToString(CultureInfo.InvariantCulture)} using the last closed 1-minute candle. No real funds moved.");
    }

    /// <summary>
    /// Stable identity for orders a person submits by hand rather than a
    /// strategy template.
    /// </summary>
    public static Guid ManualPaperStrategyId { get; } = new("6d1f5f3a-1a2b-4c8d-9e7f-0a1b2c3d4e5f");

    private static bool IsOpposite(PositionDirection direction, OrderSide side) =>
        (direction == PositionDirection.DirectionLong && side == OrderSide.Sell)
        || (direction == PositionDirection.DirectionShort && side == OrderSide.Buy);

    private async Task<Position?> FindOpenPositionAsync(
        Guid userId,
        string symbol,
        CancellationToken cancellationToken)
    {
        var open = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);

        return open.FirstOrDefault(position =>
            string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
            && position.Status == PositionStatus.Open);
    }

    private async Task<FillPrice> ResolveFillPriceAsync(
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
        catch (MarketDataSourceException exception)
        {
            return FillPrice.Unavailable(
                PaperTradeOutcome.PriceUnavailable,
                $"No price could be loaded for {symbol}. {exception.Message}");
        }

        // Only closed candles price a fill. The forming bar's close is not
        // final, so filling against it would use a number that has not settled.
        var latestClosed = candles
            .Where(candle => candle.CanBeUsedForClosedCandleSignal)
            .OrderBy(candle => candle.CloseTimeUtc)
            .LastOrDefault();

        if (latestClosed is null)
        {
            return FillPrice.Unavailable(
                PaperTradeOutcome.PriceUnavailable,
                $"No closed candle is available for {symbol}, so there is no settled price to fill against.");
        }

        var age = now - latestClosed.CloseTimeUtc;
        if (age > MaxPriceAge)
        {
            return FillPrice.Unavailable(
                PaperTradeOutcome.PriceStale,
                $"The most recent closed candle for {symbol} is {Math.Round(age.TotalMinutes)} minutes old. Orders are blocked while market data is stale.");
        }

        if (latestClosed.Close <= 0m)
        {
            return FillPrice.Unavailable(
                PaperTradeOutcome.PriceUnavailable,
                $"The last closed candle for {symbol} reported a non-positive close, which cannot price a fill.");
        }

        return FillPrice.Resolved(latestClosed.Close);
    }

    private sealed class FillPrice
    {
        private FillPrice(PaperTradeOutcome outcome, string message, decimal price)
        {
            Outcome = outcome;
            Message = message;
            Price = price;
        }

        public PaperTradeOutcome Outcome { get; }

        public string Message { get; }

        public decimal Price { get; }

        public static FillPrice Resolved(decimal price) =>
            new(PaperTradeOutcome.Filled, string.Empty, price);

        public static FillPrice Unavailable(PaperTradeOutcome outcome, string message) =>
            new(outcome, message, 0m);
    }
}
