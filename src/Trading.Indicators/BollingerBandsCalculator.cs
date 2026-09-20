using Trading.MarketData;

namespace Trading.Indicators;

public readonly record struct BollingerBandsValue(decimal Middle, decimal Upper, decimal Lower, decimal StandardDeviation);

/// <summary>Bollinger Bands use population standard deviation over the closing-price window.</summary>
public sealed class BollingerBandsCalculator
{
    public BollingerBandsCalculator(int period, decimal standardDeviationMultiplier = 2m)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        ArgumentOutOfRangeException.ThrowIfNegative(standardDeviationMultiplier);
        Period = period;
        StandardDeviationMultiplier = standardDeviationMultiplier;
    }

    public int Period { get; }

    public decimal StandardDeviationMultiplier { get; }

    public IndicatorResult<BollingerBandsValue> Calculate(IReadOnlyList<Candle> candles)
    {
        ClosedCandleSeries.Validate(candles);
        if (candles.Count < Period)
        {
            return IndicatorResults.InsufficientHistory<BollingerBandsValue>(Period, candles.Count);
        }

        var closes = candles.Skip(candles.Count - Period).Select(candle => candle.Close).ToArray();
        var middle = ClosedCandleSeries.Average(closes);
        decimal squaredDifferenceSum = 0m;
        foreach (var close in closes)
        {
            var difference = close - middle;
            squaredDifferenceSum += difference * difference;
        }

        var standardDeviation = ClosedCandleSeries.SquareRoot(squaredDifferenceSum / Period);
        return IndicatorResults.Ready(
            new BollingerBandsValue(
                middle,
                middle + (standardDeviation * StandardDeviationMultiplier),
                middle - (standardDeviation * StandardDeviationMultiplier),
                standardDeviation),
            Period,
            candles.Count);
    }
}
