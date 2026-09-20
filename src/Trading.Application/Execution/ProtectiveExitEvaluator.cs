using System.Globalization;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Market;
using Trading.Domain.Positions;
using Trading.MarketData;

namespace Trading.Application.Execution;

public enum ProtectiveExitKind
{
    None = 0,

    /// <summary>The stop level was reached.</summary>
    StopLoss,

    /// <summary>The target level was reached.</summary>
    TakeProfit,
}

public sealed class ProtectiveExitFill
{
    internal ProtectiveExitFill(
        Position position,
        ProtectiveExitKind kind,
        decimal exitPrice,
        decimal realisedPnl,
        bool bothLevelsTouched,
        DateTimeOffset candleCloseTimeUtc)
    {
        Position = position;
        Kind = kind;
        ExitPrice = exitPrice;
        RealisedPnl = realisedPnl;
        BothLevelsTouched = bothLevelsTouched;
        CandleCloseTimeUtc = candleCloseTimeUtc;
    }

    public Position Position { get; }

    public ProtectiveExitKind Kind { get; }

    public decimal ExitPrice { get; }

    public decimal RealisedPnl { get; }

    /// <summary>
    /// True when one candle reached both the stop and the target. The order in
    /// which they were reached cannot be recovered from a candle's open, high,
    /// low and close, so the stop is taken. Reporting this lets a backtest or
    /// a user see that the result depended on an assumption rather than on
    /// observed sequence.
    /// </summary>
    public bool BothLevelsTouched { get; }

    public DateTimeOffset CandleCloseTimeUtc { get; }
}

public interface IProtectiveExitEvaluator
{
    Task<IReadOnlyList<ProtectiveExitFill>> EvaluateAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Closes paper positions whose stop or target was reached.
/// </summary>
/// <remarks>
/// <para>
/// Evaluation uses <em>closed</em> candles only. A forming bar's high and low
/// can still move, so triggering from it would close a position on a level the
/// market had not actually reached by the time the bar finished.
/// </para>
/// <para>
/// A candle records only its open, high, low and close, so when a single
/// candle reaches both levels the order of events is unknowable. This
/// evaluator resolves that as the stop, which is the worse outcome. Assuming
/// the target instead would make every strategy look better than it is in
/// exactly the situation where the truth is unknown.
/// </para>
/// <para>
/// Gaps are honoured. If a candle opens beyond a level, the fill is taken at
/// the open rather than at the level, because a market that gaps past a stop
/// does not fill at the stop price.
/// </para>
/// </remarks>
public sealed class ProtectiveExitEvaluator : IProtectiveExitEvaluator
{
    private const CandleInterval EvaluationInterval = CandleInterval.OneMinute;

    private readonly IHistoricalCandleSource _candles;
    private readonly IPositionRepository _positions;
    private readonly IAuditEventWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public ProtectiveExitEvaluator(
        IHistoricalCandleSource candles,
        IPositionRepository positions,
        IAuditEventWriter auditWriter,
        TimeProvider timeProvider)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<ProtectiveExitFill>> EvaluateAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        var open = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        var armed = open.Where(position => position.HasProtectiveExits).ToList();
        if (armed.Count == 0)
        {
            return [];
        }

        var now = _timeProvider.GetUtcNow();
        var fills = new List<ProtectiveExitFill>();

        foreach (var position in armed)
        {
            IReadOnlyList<Candle> candles;

            try
            {
                candles = await _candles
                    .FetchAsync(position.Symbol, EvaluationInterval, position.OpenedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MarketDataSourceException)
            {
                // A position that cannot be evaluated is left open. Closing it
                // on absent data would invent an exit the market never gave.
                continue;
            }

            var fill = EvaluateOne(position, candles, now);
            if (fill is null)
            {
                continue;
            }

            position.Reduce(position.Quantity);
            await _positions.UpdateAsync(position, cancellationToken).ConfigureAwait(false);

            await _auditWriter.WriteAsync(
                new AuditEvent(
                    Guid.NewGuid(),
                    userId,
                    fill.Kind == ProtectiveExitKind.StopLoss ? "PaperStopLossHit" : "PaperTakeProfitHit",
                    targetType: "PaperPosition",
                    targetId: position.Id.ToString("D", CultureInfo.InvariantCulture),
                    occurredAtUtc: now,
                    before: null,
                    after: string.Create(
                        CultureInfo.InvariantCulture,
                        $"Simulated exit of {position.Symbol} at {fill.ExitPrice}, result {fill.RealisedPnl}. Both levels touched in one candle: {fill.BothLevelsTouched}. No exchange was contacted and no real funds moved."),
                    correlationId: null),
                cancellationToken).ConfigureAwait(false);

            fills.Add(fill);
        }

        return fills;
    }

    private static ProtectiveExitFill? EvaluateOne(
        Position position,
        IReadOnlyList<Candle> candles,
        DateTimeOffset now)
    {
        foreach (var candle in candles.OrderBy(candle => candle.OpenTimeUtc))
        {
            // Only finished bars. A forming bar's high and low can still move.
            if (!candle.CanBeUsedForClosedCandleSignal)
            {
                continue;
            }

            // A bar that closed before the position opened cannot have
            // triggered it. Without this the evaluator would read history from
            // before the trade existed and close it immediately.
            if (candle.CloseTimeUtc <= position.OpenedAtUtc)
            {
                continue;
            }

            var stop = position.StopLossPrice;
            var target = position.TakeProfitPrice;

            var isLong = position.Direction == PositionDirection.DirectionLong;

            var stopTouched = stop is not null
                && (isLong ? candle.Low <= stop.Value : candle.High >= stop.Value);

            var targetTouched = target is not null
                && (isLong ? candle.High >= target.Value : candle.Low <= target.Value);

            if (!stopTouched && !targetTouched)
            {
                continue;
            }

            // Both in one bar: the sequence is unknowable from OHLC, so the
            // stop is taken. The alternative flatters every result in exactly
            // the case where the truth is unknown.
            var bothTouched = stopTouched && targetTouched;
            var kind = stopTouched ? ProtectiveExitKind.StopLoss : ProtectiveExitKind.TakeProfit;
            var level = stopTouched ? stop!.Value : target!.Value;

            var exitPrice = ResolveExitPrice(isLong, kind, level, candle.Open);

            var realised = isLong
                ? (exitPrice - position.EntryPrice) * position.Quantity
                : (position.EntryPrice - exitPrice) * position.Quantity;

            return new ProtectiveExitFill(position, kind, exitPrice, realised, bothTouched, candle.CloseTimeUtc);
        }

        return null;
    }

    /// <summary>
    /// Takes the gap into account: a market that opens beyond a level does not
    /// fill at that level.
    /// </summary>
    private static decimal ResolveExitPrice(bool isLong, ProtectiveExitKind kind, decimal level, decimal open)
    {
        if (kind == ProtectiveExitKind.StopLoss)
        {
            // A long stop fills at the open when the bar opened below it.
            return isLong ? Math.Min(level, open) : Math.Max(level, open);
        }

        // A target that gapped in the favourable direction fills at the open,
        // which is the price actually available.
        return isLong ? Math.Max(level, open) : Math.Min(level, open);
    }
}
