using System.Globalization;
using System.Text.Json;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

/// <summary>
/// Turns a Kraken OHLC payload into neutral <see cref="Candle"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// Kraken returns each bar as a positional array
/// <c>[time, open, high, low, close, vwap, volume, count]</c> where the prices
/// are strings and <c>time</c> is the bar's <em>open</em> time in Unix seconds.
/// Prices are parsed straight from those strings into <see cref="decimal"/>;
/// they never pass through a binary floating-point type.
/// </para>
/// <para>
/// The payload also carries a <c>last</c> value, which Kraken documents as the
/// id of the last committed bar and which is that bar's own open time. The bar
/// still forming is included in the same array, so a bar counts as closed when
/// its open time is at or before <c>last</c>. The exchange's own marker is used
/// rather than a comparison against local time, because a clock even slightly
/// ahead would present a partial bar as finished and let a strategy act on an
/// incomplete candle.
/// </para>
/// <para>
/// Bars are returned in ascending open-time order. Duplicates are kept rather
/// than discarded so the quality evaluator can flag them; silently dropping
/// data would hide a venue problem instead of reporting it.
/// </para>
/// </remarks>
internal static class KrakenOhlcMapper
{
    private const string LastPropertyName = "last";

    /// <summary>
    /// Kraken's positional OHLC row, shortest form the mapper will accept:
    /// time, open, high, low, close, vwap, volume.
    /// </summary>
    private const int MinimumRowLength = 7;

    private const int TimeIndex = 0;
    private const int OpenIndex = 1;
    private const int HighIndex = 2;
    private const int LowIndex = 3;
    private const int CloseIndex = 4;
    private const int VolumeIndex = 6;

    internal static IReadOnlyList<Candle> Map(string payload, string symbol, CandleInterval interval)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new MarketDataSourceException("Kraken returned an empty candle response.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException exception)
        {
            throw new MarketDataSourceException("Kraken returned a candle response that could not be read.", exception);
        }

        using (document)
        {
            ThrowIfVenueReportedError(document.RootElement);

            if (!document.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object)
            {
                throw new MarketDataSourceException("Kraken returned a candle response without a result.");
            }

            var rows = FindSeries(result);
            var last = ReadLast(result);
            var duration = TimeSpan.FromMinutes((int)interval);

            var candles = new List<Candle>(rows.GetArrayLength());

            foreach (var row in rows.EnumerateArray())
            {
                candles.Add(MapRow(row, symbol, interval, duration, last));
            }

            candles.Sort(static (left, right) => left.OpenTimeUtc.CompareTo(right.OpenTimeUtc));
            return candles;
        }
    }

    private static void ThrowIfVenueReportedError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            throw new MarketDataSourceException("Kraken returned a candle response without an error field.");
        }

        if (errors.GetArrayLength() == 0)
        {
            return;
        }

        // Kraken answers HTTP 200 even when the request failed, so the error
        // array is the only reliable signal. Reporting it as no data would let
        // a caller treat a failed fetch as a quiet market.
        var first = errors[0].GetString();
        throw new MarketDataSourceException(
            $"Kraken rejected the candle request: {first ?? "unspecified error"}.");
    }

    /// <summary>
    /// Finds the candle array in Kraken's result object.
    /// </summary>
    /// <remarks>
    /// Kraken keys the series by its own internal pair name, which frequently
    /// differs from the name that was requested, so the property is located by
    /// shape rather than by name. Exactly one series must be present; anything
    /// else means the response is not what was asked for.
    /// </remarks>
    private static JsonElement FindSeries(JsonElement result)
    {
        JsonElement? series = null;

        foreach (var property in result.EnumerateObject())
        {
            if (string.Equals(property.Name, LastPropertyName, StringComparison.Ordinal))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (series is not null)
            {
                throw new MarketDataSourceException(
                    "Kraken returned more than one candle series in a single response.");
            }

            series = property.Value;
        }

        return series ?? throw new MarketDataSourceException("Kraken returned no candle series.");
    }

    private static long ReadLast(JsonElement result)
    {
        if (result.TryGetProperty(LastPropertyName, out var last)
            && last.ValueKind == JsonValueKind.Number
            && last.TryGetInt64(out var value))
        {
            return value;
        }

        // Without Kraken's marker there is no trustworthy way to tell a
        // finished bar from a forming one, and guessing from the local clock
        // risks presenting a partial bar as closed.
        throw new MarketDataSourceException(
            "Kraken returned candles without the 'last' marker, so completed bars cannot be distinguished from the bar still forming.");
    }

    private static Candle MapRow(
        JsonElement row,
        string symbol,
        CandleInterval interval,
        TimeSpan duration,
        long last)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < MinimumRowLength)
        {
            throw new MarketDataSourceException("Kraken returned a candle row in an unexpected shape.");
        }

        var openTimeSeconds = ReadEpochSeconds(row[TimeIndex]);
        var openTimeUtc = DateTimeOffset.FromUnixTimeSeconds(openTimeSeconds);

        return new Candle(
            symbol,
            interval,
            openTimeUtc,
            openTimeUtc.Add(duration),
            ReadDecimal(row[OpenIndex], "open"),
            ReadDecimal(row[HighIndex], "high"),
            ReadDecimal(row[LowIndex], "low"),
            ReadDecimal(row[CloseIndex], "close"),
            ReadDecimal(row[VolumeIndex], "volume"),
            isClosed: openTimeSeconds <= last,
            isDerived: false);
    }

    private static long ReadEpochSeconds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
        {
            return value;
        }

        throw new MarketDataSourceException("Kraken returned a candle without a usable timestamp.");
    }

    /// <summary>
    /// Reads a Kraken numeric field as <see cref="decimal"/>.
    /// </summary>
    /// <remarks>
    /// Kraken sends prices and volumes as strings. They are parsed with the
    /// invariant culture so a host configured for a comma decimal separator
    /// cannot misread a price, and as decimal so no value is ever rounded
    /// through binary floating point.
    /// </remarks>
    private static decimal ReadDecimal(JsonElement element, string fieldName)
    {
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };

        if (text is not null
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new MarketDataSourceException($"Kraken returned a candle whose {fieldName} could not be read as a number.");
    }
}
