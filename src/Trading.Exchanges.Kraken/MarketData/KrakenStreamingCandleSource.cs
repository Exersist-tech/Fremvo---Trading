using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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

        var groups = SplitByInterval(requested);
        if (groups.Length == 1)
        {
            await foreach (var candle in StreamIntervalAsync(groups[0], cancellationToken).ConfigureAwait(false))
                yield return candle;
            yield break;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<Candle>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var remaining = groups.Length;
        async Task ProduceAsync(CandleSubscription[] group)
        {
            try
            {
                await foreach (var candle in StreamIntervalAsync(group, linked.Token).ConfigureAwait(false))
                    await channel.Writer.WriteAsync(candle, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException) when (linked.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // Publish the original stream failure and stop all other interval sockets.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                channel.Writer.TryComplete(exception);
                await linked.CancelAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                    channel.Writer.TryComplete();
            }
        }

        var producers = groups.Select(ProduceAsync).ToArray();
        try
        {
            await foreach (var candle in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return candle;
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(producers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
        }
    }

    internal static CandleSubscription[][] SplitByInterval(IReadOnlyCollection<CandleSubscription> requested) =>
        requested.GroupBy(subscription => subscription.Interval)
            .Select(group => group.ToArray())
            .ToArray();

    private static async IAsyncEnumerable<Candle> StreamIntervalAsync(
        CandleSubscription[] requested,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var originalsByStream = requested
            .GroupBy(subscription => new CandleSubscription(
                KrakenV2SymbolNames.ForPublicStream(subscription.Symbol), subscription.Interval))
            .ToDictionary(group => group.Key, group => group.ToArray());
        using var socket = CreatePublicSocket();
        await socket.ConnectAsync(Endpoint, cancellationToken).ConfigureAwait(false);

        var message = KrakenOhlcV2Protocol.BuildSubscribeMessage(
            originalsByStream.Keys.Select(subscription => subscription.Symbol),
            KrakenIntervalMap.ToKrakenMinutes(requested[0].Interval));
        var bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        var active = new Dictionary<CandleSubscription, Candle>();
        while (socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            var batch = KrakenOhlcV2Protocol.Parse(payload);
            if (batch.IsSnapshot)
            {
                foreach (var forming in KrakenOhlcV2Protocol.LatestSnapshotCandles(batch.Candles))
                {
                    var key = new CandleSubscription(forming.Symbol, forming.Interval);
                    if (originalsByStream.ContainsKey(key)
                        && (!active.TryGetValue(key, out var previous)
                            || forming.OpenTimeUtc >= previous.OpenTimeUtc))
                        active[key] = forming;
                }
                continue;
            }

            foreach (var forming in batch.Candles.OrderBy(candle => candle.OpenTimeUtc))
            {
                var key = new CandleSubscription(forming.Symbol, forming.Interval);
                if (!originalsByStream.TryGetValue(key, out var originals))
                {
                    continue;
                }

                var closed = Advance(active, forming);
                if (closed is not null)
                {
                    foreach (var original in originals)
                        yield return AsRequested(closed, original.Symbol);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new MarketDataSourceException("Kraken closed the public OHLC stream.");
    }

    internal static Candle? Advance(
        Dictionary<CandleSubscription, Candle> active,
        Candle forming)
    {
        var key = new CandleSubscription(forming.Symbol, forming.Interval);
        if (!active.TryGetValue(key, out var previous))
        {
            active[key] = forming;
            return null;
        }
        if (forming.OpenTimeUtc > previous.OpenTimeUtc)
        {
            active[key] = forming;
            return AsClosed(previous);
        }
        if (forming.OpenTimeUtc == previous.OpenTimeUtc)
            active[key] = forming;
        return null;
    }

    internal static Candle AsClosed(Candle candle) => new(
        candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
        candle.Open, candle.High, candle.Low, candle.Close, candle.Volume,
        isClosed: true, candle.IsDerived,
        candle.QualityFlags.Where(issue => issue != DataQualityIssue.Incomplete).ToArray());

    internal static Candle AsRequested(Candle candle, string requestedSymbol) =>
        string.Equals(candle.Symbol, requestedSymbol, StringComparison.Ordinal)
            ? candle
            : new Candle(requestedSymbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                candle.Open, candle.High, candle.Low, candle.Close, candle.Volume,
                candle.IsClosed, candle.IsDerived, candle.QualityFlags);

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
    internal sealed record Batch(bool IsSnapshot, IReadOnlyCollection<Candle> Candles);

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

    internal static IReadOnlyCollection<Candle> Map(string payload) => Parse(payload).Candles;

    internal static IReadOnlyCollection<Candle> LatestSnapshotCandles(IEnumerable<Candle> candles) =>
        candles.GroupBy(candle => new CandleSubscription(candle.Symbol, candle.Interval))
            .Select(group => group.MaxBy(candle => candle.OpenTimeUtc)!)
            .ToArray();

    internal static Batch Parse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.False)
            {
                var reason = root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
                throw new MarketDataSourceException(
                    string.IsNullOrWhiteSpace(reason)
                        ? "Kraken rejected a public OHLC subscription."
                        : $"Kraken rejected a public OHLC subscription: {reason}");
            }
            if (!root.TryGetProperty("channel", out var channel)
                || !string.Equals(channel.GetString(), "ohlc", StringComparison.Ordinal)
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return new(false, Array.Empty<Candle>());
            }

            var isSnapshot = root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "snapshot", StringComparison.Ordinal);
            return new(isSnapshot, data.EnumerateArray().Select(MapRow).ToArray());
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
