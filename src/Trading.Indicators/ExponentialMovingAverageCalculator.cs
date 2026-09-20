using Trading.MarketData;

namespace Trading.Indicators;

/// <summary>EMA is seeded with the SMA of the first complete period, then uses 2 / (period + 1).</summary>
public sealed class ExponentialMovingAverageCalculator
{
    public ExponentialMovingAverageCalculator(int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        Period = period;
    }

    public int Period { get; }

    public IndicatorResult<decimal> Calculate(IReadOnlyList<Candle> candles)
    {
        ClosedCandleSeries.Validate(candles);
        if (candles.Count < Period)
        {
            return IndicatorResults.InsufficientHistory<decimal>(Period, candles.Count);
        }

        var closes = candles.Select(candle => candle.Close).ToArray();
        return IndicatorResults.Ready(CalculateSeeded(closes, Period), Period, candles.Count);
    }

    internal static decimal CalculateSeeded(IReadOnlyList<decimal> values, int period)
    {
        var ema = ClosedCandleSeries.Average(values.Take(period).ToArray());
        var multiplier = 2m / (period + 1m);
        for (var index = period; index < values.Count; index++)
        {
            ema += (values[index] - ema) * multiplier;
        }

        return ema;
    }
}
