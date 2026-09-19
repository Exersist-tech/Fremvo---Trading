namespace Trading.Indicators;

public sealed class SimpleMovingAverageCalculator : IIndicatorCalculator
{
    public SimpleMovingAverageCalculator(int period)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
        }

        Period = period;
        Definition = new IndicatorDefinition(
            "SMA",
            "Simple moving average of the last n values.",
            "Close prices",
            "Average price across the selected window.");
    }

    public string Name => "SMA";

    public IndicatorDefinition Definition { get; }

    public int Period { get; }

    public decimal Calculate(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < Period)
        {
            throw new InvalidOperationException("Insufficient values for requested SMA period.");
        }

        return values.Take(Period).Average();
    }
}
