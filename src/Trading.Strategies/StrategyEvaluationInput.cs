using System.Collections.ObjectModel;
using Trading.MarketData;

namespace Trading.Strategies;

public sealed class StrategyEvaluationInput
{
    public StrategyEvaluationInput(
        StrategyTemplateId templateId,
        StrategyParameterSet parameters,
        StrategyState state,
        IReadOnlyList<Candle> closedCandles)
    {
        ArgumentNullException.ThrowIfNull(templateId);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(closedCandles);

        if (!templateId.Equals(state.TemplateId))
        {
            throw new ArgumentException("Strategy state belongs to a different template.", nameof(state));
        }

        if (closedCandles.Count == 0)
        {
            throw new ArgumentException("Strategy evaluation requires at least one closed candle.", nameof(closedCandles));
        }

        var copiedCandles = closedCandles.ToArray();
        ValidateClosedChronologicalCandles(copiedCandles, nameof(closedCandles));

        var asOfUtc = copiedCandles[^1].CloseTimeUtc;
        if (state.LastEvaluatedCloseUtc > asOfUtc)
        {
            throw new ArgumentException("Strategy state cannot be newer than the evaluation candle.", nameof(state));
        }

        TemplateId = templateId;
        Parameters = parameters;
        State = state;
        ClosedCandles = new ReadOnlyCollection<Candle>(copiedCandles);
        AsOfUtc = asOfUtc;
    }

    public StrategyTemplateId TemplateId { get; }

    public StrategyParameterSet Parameters { get; }

    public StrategyState State { get; }

    public IReadOnlyList<Candle> ClosedCandles { get; }

    /// <summary>
    /// The close time of the newest visible candle. No candle after this time
    /// is accepted, making the contract closed-candle-only by construction.
    /// </summary>
    public DateTimeOffset AsOfUtc { get; }

    private static void ValidateClosedChronologicalCandles(
        Candle[] candles,
        string parameterName)
    {
        var first = candles[0] ?? throw new ArgumentException("Candle series cannot contain null values.", parameterName);
        for (var index = 0; index < candles.Length; index++)
        {
            var candle = candles[index] ?? throw new ArgumentException("Candle series cannot contain null values.", parameterName);
            if (!candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal)
            {
                throw new ArgumentException("Strategies require candles safe for closed-candle signals.", parameterName);
            }

            if (!string.Equals(candle.Symbol, first.Symbol, StringComparison.OrdinalIgnoreCase)
                || candle.Interval != first.Interval)
            {
                throw new ArgumentException("All candles must have the same symbol and interval.", parameterName);
            }

            if (index > 0)
            {
                var previous = candles[index - 1];
                if (candle.OpenTimeUtc <= previous.OpenTimeUtc || candle.CloseTimeUtc <= previous.CloseTimeUtc)
                {
                    throw new ArgumentException("Candles must be ordered by strictly ascending open and close times.", parameterName);
                }
            }
        }
    }
}
