using Trading.MarketData;

namespace Trading.Indicators;

public sealed class SimpleMovingAverageCalculator
{
    public SimpleMovingAverageCalculator(int period)
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

        var closes = candles.Skip(candles.Count - Period).Select(candle => candle.Close).ToArray();
        return IndicatorResults.Ready(ClosedCandleSeries.Average(closes), Period, candles.Count);
    }
}
