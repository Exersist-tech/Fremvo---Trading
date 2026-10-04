using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

public sealed class KrakenStreamingOrderBookSource : IStreamingOrderBookSource
{
    internal static readonly Uri Endpoint = new("wss://ws.kraken.com/v2");

    public async IAsyncEnumerable<OrderBookSnapshot> StreamAsync(
        string symbol,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Length > 40
            || normalized.Count(character => character == '/') != 1
            || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '/' && character != '-'))
        {
            throw new ArgumentException("A normalized Kraken market symbol is required.", nameof(symbol));
        }
        var streamSymbol = KrakenV2SymbolNames.ForPublicStream(normalized);

        using var socket = CreatePublicSocket();
        await socket.ConnectAsync(Endpoint, cancellationToken).ConfigureAwait(false);
        var subscribe = KrakenOrderBookV2Protocol.BuildSubscribeMessage(streamSymbol, depth: 10);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(subscribe),
            WebSocketMessageType.Text,
            true,
            cancellationToken).ConfigureAwait(false);

        var book = new KrakenOrderBookV2Protocol.BookState(streamSymbol, depth: 10);
        while (socket.State == WebSocketState.Open)
        {
            var payload = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
            var snapshot = KrakenOrderBookV2Protocol.Apply(payload, book);
            if (snapshot is not null)
                yield return snapshot with { Symbol = normalized };
        }
    }

    internal static ClientWebSocket CreatePublicSocket() => new();

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[16 * 1024]);
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new MarketDataSourceException("Kraken closed the public order-book stream.");
            if (result.MessageType != WebSocketMessageType.Text)
                throw new MarketDataSourceException("Kraken returned a non-text order-book message.");
            await message.WriteAsync(
                buffer.Array!.AsMemory(buffer.Offset, result.Count),
                cancellationToken).ConfigureAwait(false);
            if (message.Length > 1_048_576)
                throw new MarketDataSourceException("Kraken order-book message exceeded the safe size limit.");
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(message.ToArray());
    }
}

internal static class KrakenOrderBookV2Protocol
{
    private const int ChecksumDepth = 10;

    internal static string BuildSubscribeMessage(string symbol, int depth) =>
        JsonSerializer.Serialize(new
        {
            method = "subscribe",
            @params = new
            {
                channel = "book",
                symbol = new[] { symbol },
                depth,
                snapshot = true
            }
        });

    internal static OrderBookSnapshot? Apply(string payload, BookState state)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new MarketDataSourceException("Kraken returned a malformed order-book message.");

            if (root.TryGetProperty("success", out var successElement)
                && successElement.ValueKind == JsonValueKind.False)
            {
                var reason = root.TryGetProperty("error", out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString()
                    : null;
                throw new MarketDataSourceException(
                    string.IsNullOrWhiteSpace(reason)
                        ? "Kraken rejected the public order-book subscription."
                        : $"Kraken rejected the public order-book subscription: {reason}");
            }

            if (!root.TryGetProperty("channel", out var channel)
                || channel.ValueKind != JsonValueKind.String
                || channel.GetString() != "book"
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                return null;
            }

            var dataItem = data[0];
            if (dataItem.ValueKind != JsonValueKind.Object
                || !dataItem.TryGetProperty("symbol", out var symbolElement)
                || symbolElement.ValueKind != JsonValueKind.String
                || !string.Equals(symbolElement.GetString(), state.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var type = root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            if (type is not ("snapshot" or "update"))
                return null;

            if (type == "snapshot")
            {
                state.Bids.Clear();
                state.Asks.Clear();
            }
            else if (!state.HasSnapshot)
            {
                return null;
            }

            ApplySide(dataItem, "bids", state.Bids);
            ApplySide(dataItem, "asks", state.Asks);
            Truncate(state.Bids, state.Depth, descending: true);
            Truncate(state.Asks, state.Depth, descending: false);

            if (!dataItem.TryGetProperty("checksum", out var checksumElement)
                || !checksumElement.TryGetUInt32(out var expectedChecksum))
            {
                throw new MarketDataSourceException("Kraken order-book update did not contain a valid checksum.");
            }

            var actualChecksum = CalculateChecksum(state.Bids, state.Asks);
            if (actualChecksum != expectedChecksum)
                throw new MarketDataSourceException("Kraken order-book checksum mismatch; the book is no longer synchronized.");

            if (!dataItem.TryGetProperty("timestamp", out var timestampElement)
                || timestampElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var asOfUtc))
            {
                throw new MarketDataSourceException("Kraken order-book message did not contain a valid UTC timestamp.");
            }

            state.HasSnapshot = true;
            return new(
                state.Symbol,
                asOfUtc,
                state.Bids.Reverse().Select(level => new OrderBookLevel(level.Value.Price, level.Value.Quantity)).ToArray(),
                state.Asks.Select(level => new OrderBookLevel(level.Value.Price, level.Value.Quantity)).ToArray(),
                expectedChecksum,
                IsSynchronized: true);
        }
        catch (JsonException exception)
        {
            throw new MarketDataSourceException("Kraken returned a malformed order-book message.", exception);
        }
    }

    internal static uint CalculateChecksum(
        SortedDictionary<decimal, BookLevel> bids,
        SortedDictionary<decimal, BookLevel> asks)
    {
        var builder = new StringBuilder();
        foreach (var level in asks.Take(ChecksumDepth))
            AppendChecksumLevel(builder, level.Value);
        foreach (var level in bids.Reverse().Take(ChecksumDepth))
            AppendChecksumLevel(builder, level.Value);
        return CalculateCrc32(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> bytes)
    {
        var checksum = uint.MaxValue;
        foreach (var value in bytes)
        {
            checksum ^= value;
            for (var bit = 0; bit < 8; bit++)
                checksum = (checksum & 1) == 0 ? checksum >> 1 : (checksum >> 1) ^ 0xEDB88320u;
        }
        return ~checksum;
    }

    private static void ApplySide(
        JsonElement dataItem,
        string sideName,
        SortedDictionary<decimal, BookLevel> side)
    {
        if (!dataItem.TryGetProperty(sideName, out var updates)
            || updates.ValueKind != JsonValueKind.Array)
            return;

        foreach (var update in updates.EnumerateArray())
        {
            var rawPrice = ReadNumber(update, "price");
            var rawQuantity = ReadNumber(update, "qty");
            if (!decimal.TryParse(rawPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
                || !decimal.TryParse(rawQuantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity)
                || price <= 0m
                || quantity < 0m)
            {
                throw new MarketDataSourceException("Kraken order-book level contained an invalid price or quantity.");
            }

            if (quantity == 0m)
                side.Remove(price);
            else
                side[price] = new BookLevel(price, quantity, rawPrice, rawQuantity);
        }
    }

    private static string ReadNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            throw new MarketDataSourceException($"Kraken order-book level omitted {name}.");
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static void Truncate(
        SortedDictionary<decimal, BookLevel> side,
        int depth,
        bool descending)
    {
        IEnumerable<KeyValuePair<decimal, BookLevel>> levels = descending ? side.Reverse() : side;
        var retained = levels
            .Take(depth)
            .Select(level => level.Key)
            .ToHashSet();
        foreach (var price in side.Keys.Where(price => !retained.Contains(price)).ToArray())
            side.Remove(price);
    }

    private static void AppendChecksumLevel(StringBuilder builder, BookLevel level)
    {
        AppendNumber(builder, level.RawPrice);
        AppendNumber(builder, level.RawQuantity);
    }

    private static void AppendNumber(StringBuilder builder, string value)
    {
        var digits = value.Replace(".", string.Empty, StringComparison.Ordinal);
        var firstNonZero = 0;
        while (firstNonZero < digits.Length && digits[firstNonZero] == '0')
            firstNonZero++;
        if (firstNonZero < digits.Length)
            builder.Append(digits.AsSpan(firstNonZero));
    }

    internal sealed class BookState(string symbol, int depth)
    {
        internal string Symbol { get; } = symbol;
        internal int Depth { get; } = depth;
        internal bool HasSnapshot { get; set; }
        internal SortedDictionary<decimal, BookLevel> Bids { get; } = new();
        internal SortedDictionary<decimal, BookLevel> Asks { get; } = new();
    }

    internal sealed record BookLevel(decimal Price, decimal Quantity, string RawPrice, string RawQuantity);
}
