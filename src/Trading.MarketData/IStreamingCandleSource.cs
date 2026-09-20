using Trading.Domain.Market;

namespace Trading.MarketData;

/// <summary>Exchange-neutral public candle stream.</summary>
public interface IStreamingCandleSource
{
    IAsyncEnumerable<Candle> StreamAsync(
        IReadOnlyCollection<CandleSubscription> subscriptions,
        CancellationToken cancellationToken = default);
}

public sealed record CandleSubscription
{
    public CandleSubscription(string symbol, CandleInterval interval)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (interval == CandleInterval.None)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval is required.");
        }

        Symbol = symbol.Trim().ToUpperInvariant();
        Interval = interval;
    }

    public string Symbol { get; }

    public CandleInterval Interval { get; }
}
