namespace Trading.Indicators;

public sealed class MovingAverageConvergenceDivergenceCalculator : IIndicatorCalculator
{
    public MovingAverageConvergenceDivergenceCalculator(int fastPeriod, int slowPeriod, int signalPeriod)
    {
        if (fastPeriod <= 0 || slowPeriod <= 0 || signalPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fastPeriod), "Indicator periods must be positive.");
        }

        if (fastPeriod >= slowPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(slowPeriod), "The slow period must be greater than the fast period.");
        }

        FastPeriod = fastPeriod;
        SlowPeriod = slowPeriod;
        SignalPeriod = signalPeriod;
        Definition = new IndicatorDefinition(
            "MACD",
            "MACD histogram using fast, slow, and signal moving averages.",
            "Close prices",
            "Difference between short and long EMAs, less the signal line.");
    }

    public string Name => "MACD";

    public IndicatorDefinition Definition { get; }

    public int FastPeriod { get; }

    public int SlowPeriod { get; }

    public int SignalPeriod { get; }

    public decimal Calculate(IReadOnlyList<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count < SlowPeriod)
        {
            throw new InvalidOperationException("Insufficient values for the requested MACD window.");
        }

        var fastEma = new ExponentialMovingAverageCalculator(FastPeriod);
        var slowEma = new ExponentialMovingAverageCalculator(SlowPeriod);

        var macd = fastEma.Calculate(values) - slowEma.Calculate(values);
        var signalEma = new ExponentialMovingAverageCalculator(SignalPeriod);
        var signal = signalEma.Calculate(values.TakeLast(Math.Min(values.Count, SignalPeriod)).ToArray());

        return macd - signal;
    }
}
