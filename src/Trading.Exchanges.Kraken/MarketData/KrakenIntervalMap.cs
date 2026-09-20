using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

/// <summary>
/// Translates the platform's neutral intervals into Kraken's OHLC interval
/// parameter.
/// </summary>
/// <remarks>
/// Kraken accepts 1, 5, 15, 30, 60, 240, 1440, 10080 and 21600 minutes. It has
/// no ten-minute or four-day candle, so <see cref="CandleInterval.TenMinutes"/>
/// and <see cref="CandleInterval.FourDays"/> are refused rather than quietly
/// answered with the nearest size. Derived bars are built locally from their
/// explicitly required closed constituents and marked as derived.
/// </remarks>
internal static class KrakenIntervalMap
{
    internal const string VenueName = "Kraken";

    internal static int ToKrakenMinutes(CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute => 1,
        CandleInterval.FiveMinutes => 5,
        CandleInterval.FifteenMinutes => 15,
        CandleInterval.ThirtyMinutes => 30,
        CandleInterval.OneHour => 60,
        CandleInterval.FourHours => 240,
        CandleInterval.OneDay => 1440,
        CandleInterval.TenMinutes => throw new MarketDataIntervalNotSupportedException(interval, VenueName),
        CandleInterval.FourDays => throw new MarketDataIntervalNotSupportedException(interval, VenueName),
        CandleInterval.None => throw new ArgumentOutOfRangeException(nameof(interval), "An interval is required."),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, "Unknown interval."),
    };

    internal static bool IsNativelySupported(CandleInterval interval) =>
        interval is CandleInterval.OneMinute
            or CandleInterval.FiveMinutes
            or CandleInterval.FifteenMinutes
            or CandleInterval.ThirtyMinutes
            or CandleInterval.OneHour
            or CandleInterval.FourHours
            or CandleInterval.OneDay;
}
