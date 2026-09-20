using Trading.Domain.Market;

namespace Trading.Strategies;

/// <summary>
/// Immutable, ordered timeframe roles. Execution may equal Signal; it may
/// never be coarser, and Regime may never be finer than Signal.
/// </summary>
public sealed class StrategyTimeframeConfiguration : IEquatable<StrategyTimeframeConfiguration>
{
    public StrategyTimeframeConfiguration(
        CandleInterval regime,
        CandleInterval signal,
        CandleInterval execution)
    {
        ValidateSupported(regime, nameof(regime));
        ValidateSupported(signal, nameof(signal));
        ValidateSupported(execution, nameof(execution));

        if ((int)regime < (int)signal)
        {
            throw new ArgumentException("Regime must not be lower granularity than Signal.", nameof(regime));
        }

        if ((int)signal < (int)execution)
        {
            throw new ArgumentException("Execution must not be lower granularity than Signal.", nameof(execution));
        }

        Regime = regime;
        Signal = signal;
        Execution = execution;
    }

    public CandleInterval Regime { get; }
    public CandleInterval Signal { get; }
    public CandleInterval Execution { get; }

    public bool Equals(StrategyTimeframeConfiguration? other) =>
        other is not null
        && Regime == other.Regime
        && Signal == other.Signal
        && Execution == other.Execution;

    public override bool Equals(object? obj) => Equals(obj as StrategyTimeframeConfiguration);

    public override int GetHashCode() => HashCode.Combine(Regime, Signal, Execution);

    internal static void ValidateSupported(CandleInterval interval, string parameterName)
    {
        if (interval == CandleInterval.None || !Enum.IsDefined(interval))
        {
            throw new ArgumentOutOfRangeException(parameterName, "A supported, concrete candle interval is required.");
        }
    }
}
