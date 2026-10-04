using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Backtesting;

/// <summary>Requires a complete, closed, contiguous payload matching its immutable manifest.</summary>
public static class HistoricalCandleEvidenceValidator
{
    public static void Validate(HistoricalDataset dataset, IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count != dataset.CandleCount)
            throw new ArgumentException("Supplied candle count must exactly match the dataset manifest.", nameof(candles));

        var interval = dataset.Interval switch
        {
            "1M" => CandleInterval.OneMinute,
            "5M" => CandleInterval.FiveMinutes,
            "10M" => CandleInterval.TenMinutes,
            "15M" => CandleInterval.FifteenMinutes,
            "30M" => CandleInterval.ThirtyMinutes,
            "1H" => CandleInterval.OneHour,
            "4H" => CandleInterval.FourHours,
            "1D" => CandleInterval.OneDay,
            _ => throw new ArgumentOutOfRangeException(nameof(dataset))
        };
        var duration = TimeSpan.FromMinutes((int)interval);
        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index] ?? throw new ArgumentException("Candle collection cannot contain null values.", nameof(candles));
            if (!candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal
                || candle.CloseTimeUtc >= dataset.CreatedAtUtc)
                throw new ArgumentException("All candle evidence must be closed, safe, and available at dataset creation.", nameof(candles));

            if (candle.Open <= 0m || candle.Close <= 0m || candle.Low <= 0m
                || candle.High < candle.Open || candle.High < candle.Close
                || candle.Low > candle.Open || candle.Low > candle.Close)
                throw new ArgumentException("All OHLC prices must be positive and within each candle's high-low range.", nameof(candles));

            if (!string.Equals(candle.Symbol, dataset.Symbol, StringComparison.Ordinal)
                || candle.Interval != interval
                || candle.OpenTimeUtc < dataset.FromUtc
                || candle.OpenTimeUtc > dataset.ToUtc)
                throw new ArgumentException("Candle scope must exactly match the dataset manifest.", nameof(candles));

            if (index == 0 && candle.OpenTimeUtc != dataset.FromUtc
                || index == candles.Count - 1 && candle.OpenTimeUtc != dataset.ToUtc)
                throw new ArgumentException("Candle range must exactly match the dataset manifest.", nameof(candles));

            if (candle.OpenTimeUtc.Ticks % duration.Ticks != 0
                || candle.CloseTimeUtc != candle.OpenTimeUtc + duration)
                throw new ArgumentException("Candle boundaries must align exactly with the dataset interval.", nameof(candles));

            if (index > 0)
            {
                var previous = candles[index - 1];
                if (candle.OpenTimeUtc <= previous.OpenTimeUtc
                    || candle.CloseTimeUtc <= previous.CloseTimeUtc
                    || candle.OpenTimeUtc != previous.OpenTimeUtc + duration)
                    throw new ArgumentException("Candles must be complete, chronological, unique, and contiguous.", nameof(candles));
            }
        }

        if (!string.Equals(HistoricalCandleFingerprint.Compute(candles), dataset.ContentFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Supplied candle content does not match the immutable dataset fingerprint.", nameof(candles));
    }
}
