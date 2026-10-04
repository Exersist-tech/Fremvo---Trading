using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.Application.Experiments;

public sealed class ThreeSwingChannelDivergenceEvidenceQuery
{
    private const int RequiredHistory = 220;
    private readonly IHistoricalCandleSource _candles;
    private readonly TimeProvider _timeProvider;

    public ThreeSwingChannelDivergenceEvidenceQuery(
        IHistoricalCandleSource candles,
        TimeProvider timeProvider)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ThreeSwingChannelDivergenceEvidence> GetAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var market = symbol.Trim();
        var signalBoundary = AlignDown(_timeProvider.GetUtcNow(), TimeSpan.FromMinutes(5));
        var fiveMinuteTask = LoadAsync(market, CandleInterval.FiveMinutes, signalBoundary, cancellationToken);
        var oneHourTask = LoadAsync(market, CandleInterval.OneHour, signalBoundary, cancellationToken);
        var fourHourTask = LoadAsync(market, CandleInterval.FourHours, signalBoundary, cancellationToken);
        await Task.WhenAll(fiveMinuteTask, oneHourTask, fourHourTask).ConfigureAwait(false);

        var fiveMinute = await fiveMinuteTask.ConfigureAwait(false);
        var oneHour = await oneHourTask.ConfigureAwait(false);
        var fourHour = await fourHourTask.ConfigureAwait(false);
        if (fiveMinute is null || oneHour is null || fourHour is null)
        {
            return Unavailable(
                "The chart requires 220 contiguous, closed candles for 5m, 1h, and 4h; one or more series is missing, stale, or incomplete.");
        }

        return ThreeSwingChannelDivergenceModel.Evaluate(fiveMinute, oneHour, fourHour);
    }

    private async Task<Candle[]?> LoadAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset signalBoundary,
        CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var expectedClose = AlignDown(signalBoundary, duration);
        var from = expectedClose.AddTicks(-duration.Ticks * (RequiredHistory + 2L));
        var fetched = await _candles.FetchAsync(symbol, interval, from, cancellationToken).ConfigureAwait(false);
        var closed = fetched
            .Where(candle => candle.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                && candle.Interval == interval
                && candle.CanBeUsedForClosedCandleSignal
                && candle.CloseTimeUtc <= expectedClose)
            .OrderBy(candle => candle.OpenTimeUtc)
            .TakeLast(RequiredHistory)
            .ToArray();
        if (closed.Length != RequiredHistory
            || closed[^1].CloseTimeUtc != expectedClose
            || closed.Zip(closed.Skip(1), (left, right) => left.CloseTimeUtc == right.OpenTimeUtc)
                .Any(contiguous => !contiguous))
        {
            return null;
        }

        return closed;
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - value.UtcTicks % interval.Ticks, TimeSpan.Zero);

    private static ThreeSwingChannelDivergenceEvidence Unavailable(string reason) =>
        new(
            false,
            ThreeSwingDivergenceDirection.Neutral,
            Array.Empty<ThreeSwingPivot>(),
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            null,
            null,
            reason);
}
