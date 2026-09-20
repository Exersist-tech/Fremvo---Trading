namespace Trading.Indicators;

public sealed class AverageTrueRangeCalculator : IIndicatorCalculator
{
    public AverageTrueRangeCalculator(int period)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
        }

        Period = period;
        Definition = new IndicatorDefinition(
            "ATR",
            "Average true range over the selected window.",
            "High/low/close prices",
            "Average volatility over the selected window.");
    }

    public string Name => "ATR";

    public IndicatorDefinition Definition { get; }

    public int Period { get; }

    public decimal Calculate(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < Period)
        {
            throw new InvalidOperationException("Insufficient values for the requested ATR period.");
        }

        var window = values.TakeLast(Period).ToList();
        var trueRanges = new List<decimal>();

        for (var i = 1; i < window.Count; i++)
        {
            var previousClose = window[i - 1];
            var currentHigh = window[i];
            var currentLow = window[i] - 1m;
            var trueRange = Math.Max(currentHigh - currentLow, Math.Max(currentHigh - previousClose, Math.Abs(previousClose - currentLow)));
            trueRanges.Add(trueRange);
        }

        return trueRanges.Average();
    }
}
