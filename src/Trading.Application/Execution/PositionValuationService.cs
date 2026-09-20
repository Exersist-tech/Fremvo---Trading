using Trading.Domain.Market;
using Trading.Domain.Positions;
using Trading.MarketData;

namespace Trading.Application.Execution;

/// <summary>
/// An open position priced against the most recent closed candle.
/// </summary>
/// <remarks>
/// Every figure here is computed in <see cref="decimal"/> on the server. The
/// browser is sent finished numbers rather than the inputs to calculate them,
/// because arithmetic in JavaScript runs in binary floating point and would
/// report a profit or loss that does not match the platform's own books.
/// </remarks>
public sealed class ValuedPosition
{
    internal ValuedPosition(
        Position position,
        decimal? markPrice,
        DateTimeOffset? pricedAtUtc,
        bool priceIsStale,
        string? priceUnavailableReason)
    {
        Position = position;
        MarkPrice = markPrice;
        PricedAtUtc = pricedAtUtc;
        PriceIsStale = priceIsStale;
        PriceUnavailableReason = priceUnavailableReason;
    }

    public Position Position { get; }

    /// <summary>
    /// Close of the most recent closed candle, or <see langword="null"/> when
    /// no usable price was available.
    /// </summary>
    public decimal? MarkPrice { get; }

    /// <summary>Close time of the candle the valuation used.</summary>
    public DateTimeOffset? PricedAtUtc { get; }

    /// <summary>
    /// True when the pricing candle is old enough that the valuation should not
    /// be trusted. The figure is still shown, but marked, because silently
    /// presenting a stale valuation as current is what leads someone to act on
    /// a number that stopped moving.
    /// </summary>
    public bool PriceIsStale { get; }

    /// <summary>Why no price could be obtained, when that is the case.</summary>
    public string? PriceUnavailableReason { get; }

    public bool HasPrice => MarkPrice.HasValue;

    /// <summary>
    /// Unrealised profit or loss in quote currency, or <see langword="null"/>
    /// when the position could not be priced. A position with no price has an
    /// unknown result, which is not the same as a result of zero.
    /// </summary>
    public decimal? UnrealisedPnl => MarkPrice is null
        ? null
        : Position.Direction == PositionDirection.DirectionLong
            ? (MarkPrice.Value - Position.EntryPrice) * Position.Quantity
            : (Position.EntryPrice - MarkPrice.Value) * Position.Quantity;

    /// <summary>
    /// Unrealised return on the position's own notional, as a percentage.
    /// </summary>
    public decimal? UnrealisedPercent
    {
        get
        {
            if (MarkPrice is null)
            {
                return null;
            }

            var notional = Position.EntryPrice * Position.Quantity;
            if (notional == 0m)
            {
                return null;
            }

            return UnrealisedPnl!.Value / notional * 100m;
        }
    }

    /// <summary>
    /// The price at which the position would break even, ignoring fees.
    /// </summary>
    /// <remarks>
    /// Fees and spread are not modelled anywhere in paper trading yet, so this
    /// is the raw entry price. It is exposed under its own name so it is not
    /// mistaken for a fee-inclusive break-even once fees do exist.
    /// </remarks>
    public decimal BreakEvenPriceExcludingFees => Position.EntryPrice;
}

public interface IPositionValuationService
{
    Task<IReadOnlyList<ValuedPosition>> ValueOpenPositionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Prices a user's open positions against published closed candles.
/// </summary>
/// <remarks>
/// <para>
/// This service reads market data and positions. It creates no order, changes
/// no position and contacts no venue with a credential.
/// </para>
/// <para>
/// Valuation uses the most recent <em>closed</em> candle. The bar still forming
/// is excluded for the same reason it is excluded from fills: its close has not
/// settled, so a valuation built on it would move under the user and would not
/// match the price any order could have achieved.
/// </para>
/// </remarks>
public sealed class PositionValuationService : IPositionValuationService
{
    /// <summary>
    /// Beyond this age the pricing candle is reported as stale. It matches the
    /// limit paper fills use, so the chart cannot show a position as freshly
    /// valued at a price the trading path would refuse to trade on.
    /// </summary>
    private static readonly TimeSpan MaxPriceAge = TimeSpan.FromMinutes(30);

    private const CandleInterval PricingInterval = CandleInterval.OneMinute;

    private readonly IHistoricalCandleSource _candles;
    private readonly IPositionRepository _positions;
    private readonly TimeProvider _timeProvider;

    public PositionValuationService(
        IHistoricalCandleSource candles,
        IPositionRepository positions,
        TimeProvider timeProvider)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<ValuedPosition>> ValueOpenPositionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        // Scoped to the signed-in user by the repository. There is no path here
        // that can read another user's positions.
        var open = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return [];
        }

        var now = _timeProvider.GetUtcNow();
        var valued = new List<ValuedPosition>(open.Count);

        // One fetch per distinct symbol rather than one per position.
        var symbols = open
            .Select(position => position.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var prices = new Dictionary<string, PricePoint>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in symbols)
        {
            prices[symbol] = await ResolveLastClosedAsync(symbol, now, cancellationToken).ConfigureAwait(false);
        }

        foreach (var position in open)
        {
            var price = prices.TryGetValue(position.Symbol, out var found)
                ? found
                : PricePoint.Missing("No price was requested for this pair.");

            valued.Add(new ValuedPosition(
                position,
                price.Price,
                price.PricedAtUtc,
                price.Price is not null && price.PricedAtUtc is not null && now - price.PricedAtUtc.Value > MaxPriceAge,
                price.UnavailableReason));
        }

        return valued;
    }

    private async Task<PricePoint> ResolveLastClosedAsync(
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
            // A failed fetch is reported as an absent price, never as a price
            // of zero and never as an unchanged valuation. This also covers an
            // unsupported interval, which derives from the same exception.
            return PricePoint.Missing(exception.Message);
        }

        Candle? latest = null;
        foreach (var candle in candles)
        {
            if (!candle.CanBeUsedForClosedCandleSignal)
            {
                continue;
            }

            if (latest is null || candle.CloseTimeUtc > latest.CloseTimeUtc)
            {
                latest = candle;
            }
        }

        return latest is null
            ? PricePoint.Missing("No closed candle was available to price this position.")
            : new PricePoint(latest.Close, latest.CloseTimeUtc, null);
    }

    private sealed record PricePoint(decimal? Price, DateTimeOffset? PricedAtUtc, string? UnavailableReason)
    {
        internal static PricePoint Missing(string reason) => new(null, null, reason);
    }
}
