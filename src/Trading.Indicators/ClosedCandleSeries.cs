using Trading.MarketData;

namespace Trading.Indicators;

internal static class ClosedCandleSeries
{
    public static void Validate(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        if (candles.Count == 0)
        {
            return;
        }

        var first = candles[0] ?? throw new ArgumentException("Candle series cannot contain null values.", nameof(candles));
        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index] ?? throw new ArgumentException("Candle series cannot contain null values.", nameof(candles));
            if (!candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal)
            {
                throw new ArgumentException("Indicators require candles safe for closed-candle signals.", nameof(candles));
            }

            if (!string.Equals(candle.Symbol, first.Symbol, StringComparison.OrdinalIgnoreCase)
                || candle.Interval != first.Interval)
            {
                throw new ArgumentException("All candles must have the same symbol and interval.", nameof(candles));
            }

            if (index > 0)
            {
                var previous = candles[index - 1];
                if (candle.OpenTimeUtc <= previous.OpenTimeUtc || candle.CloseTimeUtc <= previous.CloseTimeUtc)
                {
                    throw new ArgumentException("Candles must be ordered by strictly ascending open and close times.", nameof(candles));
                }
            }
        }
    }

    public static decimal Average(IReadOnlyList<decimal> values)
    {
        decimal sum = 0m;
        foreach (var value in values)
        {
            sum += value;
        }

        return sum / values.Count;
    }

    // Newton iteration keeps the entire calculation in System.Decimal rather than binary floating point.
    public static decimal SquareRoot(decimal value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0m);

        if (value == 0m)
        {
            return 0m;
        }

        var estimate = value >= 1m ? value / 2m : 1m;
        for (var iteration = 0; iteration < 28; iteration++)
        {
            var next = (estimate + (value / estimate)) / 2m;
            if (next == estimate)
            {
                break;
            }

            estimate = next;
        }

        return estimate;
    }
}
