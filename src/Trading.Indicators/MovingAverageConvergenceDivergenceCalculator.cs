using Trading.MarketData;

namespace Trading.Indicators;

public readonly record struct MacdValue(decimal Line, decimal Signal, decimal Histogram);

/// <summary>MACD uses SMA-seeded EMAs; its signal line is an SMA-seeded EMA of completed MACD lines.</summary>
public sealed class MovingAverageConvergenceDivergenceCalculator
{
    public MovingAverageConvergenceDivergenceCalculator(int fastPeriod, int slowPeriod, int signalPeriod)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fastPeriod);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slowPeriod);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(signalPeriod);
        if (fastPeriod >= slowPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(slowPeriod), "Slow period must be greater than fast period.");
        }

        FastPeriod = fastPeriod;
        SlowPeriod = slowPeriod;
        SignalPeriod = signalPeriod;
    }

    public int FastPeriod { get; }

    public int SlowPeriod { get; }

    public int SignalPeriod { get; }

    public IndicatorResult<MacdValue> Calculate(IReadOnlyList<Candle> candles)
    {
        ClosedCandleSeries.Validate(candles);
        var required = SlowPeriod + SignalPeriod - 1;
        if (candles.Count < required)
        {
            return IndicatorResults.InsufficientHistory<MacdValue>(required, candles.Count);
        }

        var closes = candles.Select(candle => candle.Close).ToArray();
        var lines = new decimal[candles.Count - SlowPeriod + 1];
        for (var closeIndex = SlowPeriod - 1; closeIndex < closes.Length; closeIndex++)
        {
            var prefix = closes.Take(closeIndex + 1).ToArray();
            lines[closeIndex - SlowPeriod + 1] =
                ExponentialMovingAverageCalculator.CalculateSeeded(prefix, FastPeriod)
                - ExponentialMovingAverageCalculator.CalculateSeeded(prefix, SlowPeriod);
        }

        var line = lines[^1];
        var signal = ExponentialMovingAverageCalculator.CalculateSeeded(lines, SignalPeriod);
        return IndicatorResults.Ready(new MacdValue(line, signal, line - signal), required, candles.Count);
    }
}
