using Trading.MarketData;

namespace Trading.Indicators;

/// <summary>RSI uses Wilder smoothing, seeded from the arithmetic mean of the first period changes.</summary>
public sealed class RelativeStrengthIndexCalculator
{
    public RelativeStrengthIndexCalculator(int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        Period = period;
    }

    public int Period { get; }

    public IndicatorResult<decimal> Calculate(IReadOnlyList<Candle> candles)
    {
        ClosedCandleSeries.Validate(candles);
        var required = Period + 1;
        if (candles.Count < required)
        {
            return IndicatorResults.InsufficientHistory<decimal>(required, candles.Count);
        }

        var gains = new decimal[Period];
        var losses = new decimal[Period];
        for (var index = 1; index <= Period; index++)
        {
            AddChange(candles[index].Close - candles[index - 1].Close, out gains[index - 1], out losses[index - 1]);
        }

        var averageGain = ClosedCandleSeries.Average(gains);
        var averageLoss = ClosedCandleSeries.Average(losses);
        for (var index = required; index < candles.Count; index++)
        {
            AddChange(candles[index].Close - candles[index - 1].Close, out var gain, out var loss);
            averageGain = ((averageGain * (Period - 1)) + gain) / Period;
            averageLoss = ((averageLoss * (Period - 1)) + loss) / Period;
        }

        var value = averageLoss == 0m ? 100m : averageGain == 0m ? 0m : 100m - (100m / (1m + (averageGain / averageLoss)));
        return IndicatorResults.Ready(value, required, candles.Count);
    }

    private static void AddChange(decimal change, out decimal gain, out decimal loss)
    {
        gain = change > 0m ? change : 0m;
        loss = change < 0m ? -change : 0m;
    }
}
