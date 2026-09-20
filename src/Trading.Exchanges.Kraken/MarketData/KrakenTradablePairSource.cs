using System.Globalization;
using System.Text.Json;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

/// <summary>
/// Reads the tradable pair list and its order filters from Kraken's public
/// AssetPairs endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is public, so no API credential is involved. Listing what a
/// venue trades needs no permission on a user's account and must never consume
/// one.
/// </para>
/// <para>
/// The list changes rarely, so the result is cached for a short period. The
/// cache holds public reference data only: no user, account, balance or
/// credential is involved, so a shared cache leaks nothing between users.
/// </para>
/// </remarks>
public sealed class KrakenTradablePairSource : ITradablePairSource, IDisposable
{
    internal const string AssetPairsPath = "/0/public/AssetPairs";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyList<TradablePair>? _cached;
    private DateTimeOffset _cachedAtUtc;

    public KrakenTradablePairSource(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default)
    {
        var fresh = ReadCache();
        if (fresh is not null)
        {
            return fresh;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            fresh = ReadCache();
            if (fresh is not null)
            {
                return fresh;
            }

            var pairs = await FetchAsync(cancellationToken).ConfigureAwait(false);

            _cached = pairs;
            _cachedAtUtc = _timeProvider.GetUtcNow();
            return pairs;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private IReadOnlyList<TradablePair>? ReadCache()
    {
        var cached = _cached;
        if (cached is null)
        {
            return null;
        }

        return _timeProvider.GetUtcNow() - _cachedAtUtc < CacheLifetime ? cached : null;
    }

    private async Task<IReadOnlyList<TradablePair>> FetchAsync(CancellationToken cancellationToken)
    {
        string payload;
        try
        {
            using var response = await _httpClient
                .GetAsync(new Uri(AssetPairsPath, UriKind.Relative), cancellationToken)
                .ConfigureAwait(false);

            payload = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new MarketDataSourceException("Kraken could not be reached to load the pair list.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MarketDataSourceException("The request to Kraken for the pair list timed out.", exception);
        }

        return KrakenAssetPairMapper.Map(payload);
    }

    public void Dispose() => _refreshLock.Dispose();
}

/// <summary>
/// Turns a Kraken AssetPairs payload into neutral <see cref="TradablePair"/>
/// instances.
/// </summary>
internal static class KrakenAssetPairMapper
{
    internal static IReadOnlyList<TradablePair> Map(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new MarketDataSourceException("Kraken returned an empty pair response.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new MarketDataSourceException("Kraken returned a pair response that could not be read.", exception);
        }

        using (document)
        {
            ThrowIfVenueReportedError(document.RootElement);

            if (!document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                throw new MarketDataSourceException("Kraken returned a pair response without a result.");
            }

            var pairs = new List<TradablePair>();

            foreach (var entry in result.EnumerateObject())
            {
                var pair = TryMapPair(entry);
                if (pair is not null)
                {
                    pairs.Add(pair);
                }
            }

            if (pairs.Count == 0)
            {
                // An empty list after a successful response means the payload
                // shape is not what this mapper understands. Returning nothing
                // would present as "the venue trades nothing".
                throw new MarketDataSourceException("Kraken returned no usable pairs.");
            }

            pairs.Sort(static (left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));

            return pairs;
        }
    }

    private static void ThrowIfVenueReportedError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            throw new MarketDataSourceException("Kraken returned a pair response without an error field.");
        }

        if (errors.GetArrayLength() == 0)
        {
            return;
        }

        // Kraken answers HTTP 200 even when the request failed.
        var first = errors[0].GetString();
        throw new MarketDataSourceException(
            $"Kraken rejected the pair request: {first ?? "unspecified error"}.");
    }

    private static TradablePair? TryMapPair(JsonProperty entry)
    {
        if (entry.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var value = entry.Value;

        var altName = ReadString(value, "altname");
        var wsName = ReadString(value, "wsname");
        var baseAsset = ReadString(value, "base");
        var quoteAsset = ReadString(value, "quote");

        if (altName is null || baseAsset is null || quoteAsset is null)
        {
            return null;
        }

        // Kraken exposes the order minimum and the price tick as strings.
        // A pair that does not publish them is skipped rather than defaulted:
        // a guessed filter would let the platform submit an order the venue
        // rejects, or refuse one it would have accepted.
        var minimumQuantity = ReadDecimal(value, "ordermin");
        var priceTick = ReadDecimal(value, "tick_size");
        var lotDecimals = ReadInt32(value, "lot_decimals");

        if (minimumQuantity is null or <= 0m || priceTick is null or <= 0m || lotDecimals is null or < 0 or > 18)
        {
            return null;
        }

        var quantityStep = QuantityStepFromDecimals(lotDecimals.Value);

        // Kraken marks a delisted or paused pair through 'status'. A pair that
        // is absent from that field is treated as tradable, matching the
        // venue's own default.
        var status = ReadString(value, "status");
        var isActive = status is null || string.Equals(status, "online", StringComparison.OrdinalIgnoreCase);

        return new TradablePair(
            altName,
            wsName ?? altName,
            baseAsset,
            quoteAsset,
            isActive,
            minimumQuantity.Value,
            quantityStep,
            priceTick.Value);
    }

    /// <summary>
    /// Converts a lot-decimal count into a step, for example 8 becomes
    /// 0.00000001.
    /// </summary>
    private static decimal QuantityStepFromDecimals(int lotDecimals)
    {
        var step = 1m;
        for (var i = 0; i < lotDecimals; i++)
        {
            step /= 10m;
        }

        return step;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        // Parsed straight into decimal. A venue filter that passed through a
        // binary floating-point type could round a tick into a value the venue
        // does not accept.
        return value.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : null,
            JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : null,
            _ => null
        };
    }

    private static int? ReadInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
