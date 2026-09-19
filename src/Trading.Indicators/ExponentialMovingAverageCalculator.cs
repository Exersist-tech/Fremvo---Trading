namespace Trading.Indicators;

public sealed class ExponentialMovingAverageCalculator : IIndicatorCalculator
{
    public ExponentialMovingAverageCalculator(int period)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
        }

        Period = period;
        Definition = new IndicatorDefinition(
            "EMA",
            "Exponential moving average using the selected smoothing window.",
            "Close prices",
            "Weighted average across the selected window.");
    }

    public string Name => "EMA";

    public IndicatorDefinition Definition { get; }

    public int Period { get; }

    public decimal Calculate(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            throw new InvalidOperationException("No values available for EMA calculation.");
        }

        if (values.Count == 1)
        {
            return values[0];
        }

        var multiplier = 2m / (Period + 1m);
        decimal ema = values[0];

        foreach (var value in values.Skip(1))
        {
            ema = (value - ema) * multiplier + ema;
        }

        return ema;
    }
}
