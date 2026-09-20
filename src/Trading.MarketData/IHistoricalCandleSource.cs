using Trading.Domain.Market;

namespace Trading.MarketData;

/// <summary>
/// Fetches historical candles from a venue in the platform's neutral shape.
/// </summary>
/// <remarks>
/// The port deals only in <see cref="Candle"/> and <see cref="CandleInterval"/>,
/// so nothing above it learns how any particular exchange represents a bar.
/// Implementations normalise; they do not interpret. Deciding whether the data
/// is fit to trade on is the quality evaluator's job, not the source's.
/// </remarks>
public interface IHistoricalCandleSource
{
    /// <summary>
    /// Returns candles for <paramref name="symbol"/> at <paramref name="interval"/>
    /// whose open time is at or after <paramref name="sinceUtc"/>, in ascending
    /// open-time order.
    /// </summary>
    /// <remarks>
    /// A venue may include the bar that is still forming. Such a candle is
    /// returned with <see cref="Candle.IsClosed"/> false so a caller cannot
    /// mistake a partial bar for a finished one. Implementations must throw
    /// <see cref="MarketDataIntervalNotSupportedException"/> rather than
    /// substituting a different interval when the venue has no native bar of
    /// the requested size.
    /// </remarks>
    Task<IReadOnlyList<Candle>> FetchAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A venue could not be read, or answered with something that cannot be trusted
/// as market data.
/// </summary>
public class MarketDataSourceException : Exception
{
    public MarketDataSourceException()
    {
    }

    public MarketDataSourceException(string message)
        : base(message)
    {
    }

    public MarketDataSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The venue has no native candle of the requested size.
/// </summary>
/// <remarks>
/// Signalled as a distinct failure so a caller can route to the derived-candle
/// builder. Returning a nearby interval instead would hand back bars of a
/// materially different size than the caller asked for, which is exactly the
/// kind of silent substitution the platform forbids.
/// </remarks>
public sealed class MarketDataIntervalNotSupportedException : MarketDataSourceException
{
    public MarketDataIntervalNotSupportedException()
    {
    }

    public MarketDataIntervalNotSupportedException(string message)
        : base(message)
    {
    }

    public MarketDataIntervalNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MarketDataIntervalNotSupportedException(CandleInterval interval, string venue)
        : base($"{venue} has no native {(int)interval}-minute candle. Derive it from a smaller interval instead.")
    {
        Interval = interval;
        Venue = venue;
    }

    public CandleInterval Interval { get; }

    public string? Venue { get; }
}
