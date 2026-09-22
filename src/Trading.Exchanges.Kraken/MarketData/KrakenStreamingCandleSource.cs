using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

/// <summary>Public-only Kraken WebSocket v2 OHLC stream.</summary>
public sealed class KrakenStreamingCandleSource : IStreamingCandleSource
{
    internal static readonly Uri Endpoint = new("wss://ws.kraken.com/v2");

    public async IAsyncEnumerable<Candle> StreamAsync(
        IReadOnlyCollection<CandleSubscription> subscriptions,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        var requested = subscriptions.Distinct().ToArray();
        if (requested.Length == 0)
        {
            yield break;
        }

        foreach (var subscription in requested)
        {
            if (!KrakenIntervalMap.IsNativelySupported(subscription.Interval))
            {
                throw new MarketDataIntervalNotSupportedException(subscription.Interval, KrakenIntervalMap.VenueName);
            }
        }

        using var socket = CreatePublicSocket();
        await socket.ConnectAsync(Endpoint, cancellationToken).ConfigureAwait(false);

        foreach (var group in requested.GroupBy(subscription => subscription.Interval))
        {
            var message = KrakenOhlcV2Protocol.BuildSubscribeMessage(
                group.Select(subscription => subscription.Symbol),
                KrakenIntervalMap.ToKrakenMinutes(group.Key));
            var bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }

        var active = new Dictionary<CandleSubscription, Candle>();
        while (socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            foreach (var forming in KrakenOhlcV2Protocol.Map(payload))
            {
                var key = new CandleSubscription(forming.Symbol, forming.Interval);
                if (!requested.Contains(key))
                {
                    continue;
                }

                if (!active.TryGetValue(key, out var previous))
                {
                    active[key] = forming;
                    continue;
                }

                if (forming.OpenTimeUtc > previous.OpenTimeUtc)
                {
                    yield return AsClosed(previous);
                    active[key] = forming;
                }
                else if (forming.OpenTimeUtc == previous.OpenTimeUtc)
                {
                    active[key] = forming;
                }
                else
                {
                    // A later interval is already observed, making this older
                    // interval complete even though it arrived out of order.
                    yield return AsClosed(forming);
                }
            }
        }
    }

    internal static Candle AsClosed(Candle candle) => new(
        candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
        candle.Open, candle.High, candle.Low, candle.Close, candle.Volume,
        isClosed: true, candle.IsDerived,
        candle.QualityFlags.Where(issue => issue != DataQualityIssue.Incomplete).ToArray());

    internal static ClientWebSocket CreatePublicSocket() => new();

    private static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[16 * 1024]);
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new MarketDataSourceException("Kraken closed the public OHLC stream.");
            }

            await message.WriteAsync(
                buffer.Array!.AsMemory(buffer.Offset, result.Count),
                cancellationToken).ConfigureAwait(false);
            if (message.Length > 1_048_576)
            {
                throw new MarketDataSourceException("Kraken sent an OHLC message exceeding the safe size limit.");
            }
        }
        while (!result.EndOfMessage);

        if (result.MessageType != WebSocketMessageType.Text)
        {
            throw new MarketDataSourceException("Kraken sent a non-text OHLC message.");
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }
}

internal static class KrakenOhlcV2Protocol
{
    internal static string BuildSubscribeMessage(IEnumerable<string> symbols, int interval)
    {
        var requested = symbols.Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(symbol => symbol.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0)
        {
            throw new ArgumentException("At least one symbol is required.", nameof(symbols));
        }

        return JsonSerializer.Serialize(new
        {
            method = "subscribe",
            @params = new { channel = "ohlc", symbol = requested, interval, snapshot = true },
        });
    }

    internal static IReadOnlyCollection<Candle> Map(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("channel", out var channel)
                || !string.Equals(channel.GetString(), "ohlc", StringComparison.Ordinal)
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<Candle>();
            }

            return data.EnumerateArray().Select(MapRow).ToArray();
        }
        catch (JsonException exception)
        {
            throw new MarketDataSourceException("Kraken returned a streaming OHLC message that could not be read.", exception);
        }
    }

    private static Candle MapRow(JsonElement row)
    {
        var symbol = RequiredString(row, "symbol");
        var minutes = RequiredInt(row, "interval");
        var interval = (CandleInterval)minutes;
        if (!KrakenIntervalMap.IsNativelySupported(interval))
        {
            throw new MarketDataSourceException("Kraken returned an unsupported OHLC interval.");
        }

        var openTime = RequiredTimestamp(row, "interval_begin");
        return new Candle(
            symbol, interval, openTime, openTime.AddMinutes(minutes),
            RequiredDecimal(row, "open"), RequiredDecimal(row, "high"),
            RequiredDecimal(row, "low"), RequiredDecimal(row, "close"),
            RequiredDecimal(row, "volume"), isClosed: false, isDerived: false);
    }

    private static string RequiredString(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new MarketDataSourceException($"Kraken OHLC field '{name}' is empty.")
            : throw new MarketDataSourceException($"Kraken OHLC message has no usable '{name}' field.");

    private static int RequiredInt(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : throw new MarketDataSourceException($"Kraken OHLC message has no usable '{name}' field.");

    private static DateTimeOffset RequiredTimestamp(JsonElement row, string name)
    {
        if (row.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return timestamp;
        }

        throw new MarketDataSourceException($"Kraken OHLC message has no usable '{name}' field.");
    }

    private static decimal RequiredDecimal(JsonElement row, string name)
    {
        if (row.TryGetProperty(name, out var value))
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() :
                value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;
            if (text is not null && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
        }

        throw new MarketDataSourceException($"Kraken OHLC field '{name}' is not a decimal.");
    }
}
