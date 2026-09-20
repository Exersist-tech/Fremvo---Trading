using Trading.Domain.Market;

namespace Trading.MarketData;

public static class DerivedCandleBuilder
{
    public static Candle BuildTenMinuteCandle(
        IReadOnlyList<Candle> oneMinuteCandles,
        string symbol,
        DateTimeOffset startTimeUtc,
        DateTimeOffset endTimeUtc,
        bool isClosed)
    {
        ArgumentNullException.ThrowIfNull(oneMinuteCandles);

        if (oneMinuteCandles.Count != 10)
        {
            throw new ArgumentException("Exactly 10 one-minute candles are required to derive a 10-minute candle.", nameof(oneMinuteCandles));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (endTimeUtc <= startTimeUtc)
        {
            throw new ArgumentException("The derived candle end time must be after the start time.", nameof(endTimeUtc));
        }

        var start = startTimeUtc.ToUniversalTime();
        var end = endTimeUtc.ToUniversalTime();
        if (start.Second != 0 || start.Millisecond != 0 || start.Minute % 10 != 0)
        {
            throw new ArgumentException("The derived candle must begin on a UTC ten-minute boundary.", nameof(startTimeUtc));
        }

        if (end != start.AddMinutes(10))
        {
            throw new ArgumentException("The derived candle must cover exactly ten minutes.", nameof(endTimeUtc));
        }

        var ordered = oneMinuteCandles
            .OrderBy(c => c.OpenTimeUtc)
            .ToList();

        if (ordered.Any(c => !string.Equals(c.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("All one-minute candles must match the requested symbol.", nameof(symbol));
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var candle = ordered[index];
            if (candle.Interval != CandleInterval.OneMinute ||
                !candle.IsClosed ||
                candle.OpenTimeUtc != start.AddMinutes(index) ||
                candle.CloseTimeUtc != start.AddMinutes(index + 1))
            {
                throw new ArgumentException(
                    "Constituents must be exactly ten contiguous closed one-minute candles.",
                    nameof(oneMinuteCandles));
            }
        }

        var open = ordered.First().Open;
        var high = ordered.Max(c => c.High);
        var low = ordered.Min(c => c.Low);
        var close = ordered.Last().Close;
        var volume = ordered.Sum(c => c.Volume);
        var issues = new HashSet<DataQualityIssue>(ordered.SelectMany(candle => candle.QualityFlags));

        return new Candle(
            symbol,
            CandleInterval.TenMinutes,
            start,
            end,
            open,
            high,
            low,
            close,
            volume,
            isClosed: isClosed,
            isDerived: true,
            qualityFlags: issues);
    }
}
