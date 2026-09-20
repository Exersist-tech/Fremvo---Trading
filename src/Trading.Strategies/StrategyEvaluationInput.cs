using Trading.MarketData;

namespace Trading.Strategies;

public sealed class StrategyEvaluationInput
{
    public StrategyEvaluationInput(
        StrategyTemplateId templateId,
        StrategyParameterSet parameters,
        StrategyState state,
        StrategyTimeframeConfiguration timeframes,
        IReadOnlyList<StrategyTimeframeSeries> timeframeSeries)
    {
        ArgumentNullException.ThrowIfNull(templateId);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(timeframes);
        ArgumentNullException.ThrowIfNull(timeframeSeries);

        if (!templateId.Equals(state.TemplateId))
        {
            throw new ArgumentException("Strategy state belongs to a different template.", nameof(state));
        }

        var copiedSeries = timeframeSeries.ToArray();
        if (copiedSeries.Length != 3
            || copiedSeries.Any(series => series is null)
            || copiedSeries.Select(series => series.Role).Distinct().Count() != 3)
        {
            throw new ArgumentException("Exactly one Regime, Signal, and Execution series is required.", nameof(timeframeSeries));
        }

        var byRole = copiedSeries.ToDictionary(series => series.Role);
        if (!byRole.TryGetValue(StrategyTimeframeRole.Regime, out var regime)
            || !byRole.TryGetValue(StrategyTimeframeRole.Signal, out var signal)
            || !byRole.TryGetValue(StrategyTimeframeRole.Execution, out var execution)
            || regime.Interval != timeframes.Regime
            || signal.Interval != timeframes.Signal
            || execution.Interval != timeframes.Execution)
        {
            throw new ArgumentException("Timeframe series must match their explicitly configured roles.", nameof(timeframeSeries));
        }

        var symbol = execution.ClosedCandles[0].Symbol;
        if (copiedSeries.Any(series => !string.Equals(series.ClosedCandles[0].Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("All timeframe roles must describe the same symbol.", nameof(timeframeSeries));
        }

        var asOfUtc = execution.AsOfUtc;
        if (copiedSeries.Any(series => series.AsOfUtc > asOfUtc))
        {
            throw new ArgumentException(
                "Regime and signal series cannot include a candle that closes after the execution evaluation time.",
                nameof(timeframeSeries));
        }

        if (state.LastEvaluatedCloseUtc > asOfUtc)
        {
            throw new ArgumentException("Strategy state cannot be newer than the execution evaluation candle.", nameof(state));
        }

        TemplateId = templateId;
        Parameters = parameters;
        State = state;
        Timeframes = timeframes;
        Regime = regime;
        Signal = signal;
        Execution = execution;
        ClosedCandles = signal.ClosedCandles;
        AsOfUtc = asOfUtc;
    }

    public StrategyEvaluationInput(
        StrategyTemplateId templateId,
        StrategyParameterSet parameters,
        StrategyState state,
        IReadOnlyList<Candle> closedCandles)
        : this(
            templateId,
            parameters,
            state,
            CreateSingleTimeframeConfiguration(closedCandles),
            CreateSingleTimeframeSeries(closedCandles))
    {
    }

    private static StrategyTimeframeConfiguration CreateSingleTimeframeConfiguration(
        IReadOnlyList<Candle> closedCandles)
    {
        ArgumentNullException.ThrowIfNull(closedCandles);
        if (closedCandles.Count == 0)
        {
            throw new ArgumentException("Strategy evaluation requires at least one closed candle.", nameof(closedCandles));
        }

        return new StrategyTimeframeConfiguration(
            closedCandles[0].Interval,
            closedCandles[0].Interval,
            closedCandles[0].Interval);
    }

    private static IReadOnlyList<StrategyTimeframeSeries> CreateSingleTimeframeSeries(
        IReadOnlyList<Candle> closedCandles)
    {
        ArgumentNullException.ThrowIfNull(closedCandles);
        if (closedCandles.Count == 0)
        {
            throw new ArgumentException("Strategy evaluation requires at least one closed candle.", nameof(closedCandles));
        }

        var interval = closedCandles[0].Interval;
        return
        [
            new StrategyTimeframeSeries(StrategyTimeframeRole.Regime, interval, closedCandles),
            new StrategyTimeframeSeries(StrategyTimeframeRole.Signal, interval, closedCandles),
            new StrategyTimeframeSeries(StrategyTimeframeRole.Execution, interval, closedCandles)
        ];
    }

    public StrategyTemplateId TemplateId { get; }

    public StrategyParameterSet Parameters { get; }

    public StrategyState State { get; }

    public StrategyTimeframeConfiguration Timeframes { get; }
    public StrategyTimeframeSeries Regime { get; }
    public StrategyTimeframeSeries Signal { get; }
    public StrategyTimeframeSeries Execution { get; }

    /// <summary>
    /// Compatibility view of the Signal role. New strategies should select an
    /// explicit role rather than treating the three series as interchangeable.
    /// </summary>
    public IReadOnlyList<Candle> ClosedCandles { get; }

    /// <summary>
    /// The close time of the newest visible candle. No candle after this time
    /// is accepted, making the contract closed-candle-only by construction.
    /// </summary>
    public DateTimeOffset AsOfUtc { get; }

}
