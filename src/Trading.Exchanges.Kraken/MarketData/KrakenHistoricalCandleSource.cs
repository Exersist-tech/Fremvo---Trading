using System.Globalization;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

/// <summary>
/// Reads historical candles from Kraken's public OHLC endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is public, so no API credential is involved. Fetching price
/// history needs no permission on a user's account and must never consume one.
/// </para>
/// <para>
/// Kraken caps a single OHLC response at roughly 720 bars regardless of the
/// requested start, so a caller backfilling a long history calls this
/// repeatedly, advancing <c>sinceUtc</c> past the last bar it received.
/// </para>
/// </remarks>
public sealed class KrakenHistoricalCandleSource : IHistoricalCandleSource
{
    internal const string OhlcPath = "/0/public/OHLC";

    private readonly HttpClient _httpClient;

    public KrakenHistoricalCandleSource(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<IReadOnlyList<Candle>> FetchAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        // Throws for an interval Kraken does not serve, so the caller routes to
        // the derived-candle builder instead of receiving a different size.
        var krakenInterval = KrakenIntervalMap.ToKrakenMinutes(interval);
        var requestUri = BuildRequestUri(symbol, krakenInterval, sinceUtc);

        string payload;
        try
        {
            using var response = await _httpClient
                .GetAsync(requestUri, cancellationToken)
                .ConfigureAwait(false);

            payload = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new MarketDataSourceException("Kraken could not be reached to load candles.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MarketDataSourceException("The request to Kraken for candles timed out.", exception);
        }

        return KrakenOhlcMapper.Map(payload, symbol.Trim(), interval);
    }

    internal static Uri BuildRequestUri(string symbol, int krakenInterval, DateTimeOffset sinceUtc)
    {
        var since = sinceUtc.ToUnixTimeSeconds();

        // Kraken treats 'since' as exclusive of values before it, and rejects
        // negative values outright. A caller asking for everything available
        // passes a default or pre-epoch timestamp, which becomes 0.
        if (since < 0)
        {
            since = 0;
        }

        var query = string.Create(
            CultureInfo.InvariantCulture,
            $"{OhlcPath}?pair={Uri.EscapeDataString(symbol.Trim())}&interval={krakenInterval}&since={since}");

        return new Uri(query, UriKind.Relative);
    }
}
