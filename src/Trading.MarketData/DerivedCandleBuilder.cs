using Trading.Domain.Market;

namespace Trading.MarketData;

public static class DerivedCandleBuilder
{
    private const int FourDayWindowLengthInDays = 4;

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

    /// <summary>
    /// Builds a closed four-day candle from four closed UTC day candles.
    /// Four-day windows are anchored to the Unix epoch: each window starts at
    /// 00:00:00 UTC on a date whose whole-day offset from 1970-01-01 is divisible by four.
    /// This is deliberately an epoch grid, not a locale or calendar-week boundary.
    /// </summary>
    public static Candle BuildFourDayCandle(
        IReadOnlyList<Candle> oneDayCandles,
        string symbol,
        DateTimeOffset startTimeUtc,
        DateTimeOffset endTimeUtc,
        bool isClosed)
    {
        ArgumentNullException.ThrowIfNull(oneDayCandles);

        if (oneDayCandles.Count != FourDayWindowLengthInDays)
        {
            throw new ArgumentException("Exactly four one-day candles are required to derive a four-day candle.", nameof(oneDayCandles));
        }

        if (oneDayCandles.Any(candle => candle is null))
        {
            throw new ArgumentException("Constituents cannot contain null candles.", nameof(oneDayCandles));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (!isClosed)
        {
            throw new ArgumentException("A four-day candle can only be derived as closed.", nameof(isClosed));
        }

        if (endTimeUtc <= startTimeUtc)
        {
            throw new ArgumentException("The derived candle end time must be after the start time.", nameof(endTimeUtc));
        }

        var start = startTimeUtc.ToUniversalTime();
        var end = endTimeUtc.ToUniversalTime();
        if (start.TimeOfDay != TimeSpan.Zero ||
            (start.UtcDateTime.Date - DateTime.UnixEpoch.Date).Days % FourDayWindowLengthInDays != 0)
        {
            throw new ArgumentException(
                "The derived candle must begin at a Unix-epoch-anchored four-day UTC boundary.",
                nameof(startTimeUtc));
        }

        if (end != start.AddDays(FourDayWindowLengthInDays))
        {
            throw new ArgumentException("The derived candle must cover exactly four UTC days.", nameof(endTimeUtc));
        }

        var ordered = oneDayCandles
            .OrderBy(candle => candle.OpenTimeUtc)
            .ToArray();

        if (ordered.Any(candle => !string.Equals(candle.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("All one-day candles must match the requested symbol.", nameof(symbol));
        }

        for (var index = 0; index < ordered.Length; index++)
        {
            var candle = ordered[index];
            if (candle.Interval != CandleInterval.OneDay ||
                !candle.IsClosed ||
                candle.OpenTimeUtc != start.AddDays(index) ||
                candle.CloseTimeUtc != start.AddDays(index + 1))
            {
                throw new ArgumentException(
                    "Constituents must be exactly four contiguous closed one-day candles in the requested UTC window.",
                    nameof(oneDayCandles));
            }
        }

        var issues = new HashSet<DataQualityIssue>(ordered.SelectMany(candle => candle.QualityFlags));
        return new Candle(
            symbol,
            CandleInterval.FourDays,
            start,
            end,
            ordered[0].Open,
            ordered.Max(candle => candle.High),
            ordered.Min(candle => candle.Low),
            ordered[^1].Close,
            ordered.Sum(candle => candle.Volume),
            isClosed: true,
            isDerived: true,
            qualityFlags: issues);
    }
}
