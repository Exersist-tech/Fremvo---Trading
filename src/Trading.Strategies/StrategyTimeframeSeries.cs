using System.Collections.ObjectModel;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Strategies;

/// <summary>
/// Finished, source-provided candles for exactly one strategy timeframe role.
/// The contract intentionally does not resample candles.
/// </summary>
public sealed class StrategyTimeframeSeries
{
    public StrategyTimeframeSeries(
        StrategyTimeframeRole role,
        CandleInterval interval,
        IReadOnlyList<Candle> closedCandles)
    {
        if (role == StrategyTimeframeRole.None || !Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        StrategyTimeframeConfiguration.ValidateSupported(interval, nameof(interval));
        ArgumentNullException.ThrowIfNull(closedCandles);
        if (closedCandles.Count == 0)
        {
            throw new ArgumentException("Each timeframe role requires at least one finished candle.", nameof(closedCandles));
        }

        var copiedCandles = closedCandles.ToArray();
        ValidateClosedChronologicalCandles(copiedCandles, interval, nameof(closedCandles));

        Role = role;
        Interval = interval;
        ClosedCandles = new ReadOnlyCollection<Candle>(copiedCandles);
        AsOfUtc = copiedCandles[^1].CloseTimeUtc;
    }

    public StrategyTimeframeRole Role { get; }
    public CandleInterval Interval { get; }
    public IReadOnlyList<Candle> ClosedCandles { get; }
    public DateTimeOffset AsOfUtc { get; }

    internal static void ValidateClosedChronologicalCandles(
        Candle[] candles,
        CandleInterval interval,
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
                || candle.Interval != interval)
            {
                throw new ArgumentException("All candles in a role must have the same symbol and configured interval.", parameterName);
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
