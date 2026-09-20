using Trading.MarketData;

namespace Trading.Indicators;

/// <summary>ATR uses Wilder smoothing, seeded from the first period true ranges; each later range uses the prior candle close.</summary>
public sealed class AverageTrueRangeCalculator
{
    public AverageTrueRangeCalculator(int period)
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

        var trueRanges = new decimal[candles.Count];
        trueRanges[0] = candles[0].High - candles[0].Low;
        for (var index = 1; index < candles.Count; index++)
        {
            var current = candles[index];
            var previousClose = candles[index - 1].Close;
            trueRanges[index] = decimal.Max(
                current.High - current.Low,
                decimal.Max(decimal.Abs(current.High - previousClose), decimal.Abs(current.Low - previousClose)));
        }

        var atr = ClosedCandleSeries.Average(trueRanges.Take(Period).ToArray());
        for (var index = Period; index < trueRanges.Length; index++)
        {
            atr = ((atr * (Period - 1)) + trueRanges[index]) / Period;
        }

        return IndicatorResults.Ready(atr, Period, candles.Count);
    }
}
