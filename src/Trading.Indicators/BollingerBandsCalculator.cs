namespace Trading.Indicators;

public sealed record BollingerBandsResult(decimal MiddleBand, decimal UpperBand, decimal LowerBand, decimal StandardDeviation);

public sealed class BollingerBandsCalculator
{
    public BollingerBandsCalculator(int period, decimal standardDeviationMultiplier = 2m)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
        }

        if (standardDeviationMultiplier < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(standardDeviationMultiplier), "Standard deviation multiplier cannot be negative.");
        }

        Period = period;
        StandardDeviationMultiplier = standardDeviationMultiplier;
    }

    public int Period { get; }

    public decimal StandardDeviationMultiplier { get; }

    public BollingerBandsResult Calculate(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < Period)
        {
            throw new InvalidOperationException("Insufficient values for the requested Bollinger period.");
        }

        var window = values.TakeLast(Period).ToArray();
        var middleBand = window.Average();
        var variance = window.Average(v => (v - middleBand) * (v - middleBand));
        var standardDeviation = (decimal)Math.Sqrt((double)variance);

        return new BollingerBandsResult(
            middleBand,
            middleBand + (standardDeviation * StandardDeviationMultiplier),
            middleBand - (standardDeviation * StandardDeviationMultiplier),
            standardDeviation);
    }
}
