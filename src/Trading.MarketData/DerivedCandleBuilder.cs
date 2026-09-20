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

        var ordered = oneMinuteCandles
            .OrderBy(c => c.OpenTimeUtc)
            .ToList();

        if (ordered.Any(c => !string.Equals(c.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("All one-minute candles must match the requested symbol.", nameof(symbol));
        }

        var open = ordered.First().Open;
        var high = ordered.Max(c => c.High);
        var low = ordered.Min(c => c.Low);
        var close = ordered.Last().Close;
        var volume = ordered.Sum(c => c.Volume);
        var issues = new HashSet<DataQualityIssue>(ordered.SelectMany(candle => candle.QualityFlags));
        for (var index = 0; index < ordered.Count; index++)
        {
            var expectedOpen = startTimeUtc.ToUniversalTime().AddMinutes(index);
            if (ordered[index].OpenTimeUtc != expectedOpen)
            {
                issues.Add(DataQualityIssue.Missing);
            }
        }

        for (var index = 1; index < oneMinuteCandles.Count; index++)
        {
            if (oneMinuteCandles[index].OpenTimeUtc <= oneMinuteCandles[index - 1].OpenTimeUtc)
            {
                issues.Add(oneMinuteCandles[index].OpenTimeUtc == oneMinuteCandles[index - 1].OpenTimeUtc
                    ? DataQualityIssue.Duplicate
                    : DataQualityIssue.OutOfOrder);
            }
        }

        if (endTimeUtc.ToUniversalTime() != startTimeUtc.ToUniversalTime().AddMinutes(10))
        {
            issues.Add(DataQualityIssue.Missing);
        }

        return new Candle(
            symbol,
            CandleInterval.TenMinutes,
            startTimeUtc,
            endTimeUtc,
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
