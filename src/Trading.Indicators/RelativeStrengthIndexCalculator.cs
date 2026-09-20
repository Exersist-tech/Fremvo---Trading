namespace Trading.Indicators;

public sealed class RelativeStrengthIndexCalculator : IIndicatorCalculator
{
    public RelativeStrengthIndexCalculator(int period)
    {
        if (period <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be greater than 1.");
        }

        Period = period;
        Definition = new IndicatorDefinition(
            "RSI",
            "Relative strength index over the selected lookback window.",
            "Close prices",
            "Momentum oscillator from 0 to 100.");
    }

    public string Name => "RSI";

    public IndicatorDefinition Definition { get; }

    public int Period { get; }

    public decimal Calculate(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count <= Period)
        {
            throw new InvalidOperationException("Insufficient values for the requested RSI period.");
        }

        var changes = new List<decimal>(values.Count - 1);
        for (var i = 1; i < values.Count; i++)
        {
            changes.Add(values[i] - values[i - 1]);
        }

        var gains = new List<decimal>();
        var losses = new List<decimal>();
        foreach (var change in changes.TakeLast(Period))
        {
            if (change >= 0m)
            {
                gains.Add(change);
            }
            else
            {
                losses.Add(Math.Abs(change));
            }
        }

        if (gains.Count == 0)
        {
            return 0m;
        }

        if (losses.Count == 0)
        {
            return 100m;
        }

        var avgGain = gains.Average();
        var avgLoss = losses.Average();

        if (avgLoss == 0m)
        {
            return 100m;
        }

        var relativeStrength = avgGain / avgLoss;
        return 100m - (100m / (1m + relativeStrength));
    }
}
