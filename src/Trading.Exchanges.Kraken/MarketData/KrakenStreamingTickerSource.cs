using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

public sealed record PublicTradePrice(string Symbol, decimal Price, DateTimeOffset AsOfUtc, bool IsSnapshot);

/// <summary>Public Kraken trade-triggered ticker, for display only; never a candle or execution price.</summary>
public sealed class KrakenStreamingTickerSource
{
    private readonly Uri _endpoint = new("wss://ws.kraken.com/v2");

    public async IAsyncEnumerable<PublicTradePrice> StreamAsync(
        string symbol,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var requested = symbol.Trim().ToUpperInvariant();
        var streamSymbol = KrakenV2SymbolNames.ForPublicStream(requested);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(KrakenTickerV2Protocol.Subscribe(streamSymbol)),
            WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        while (socket.State == WebSocketState.Open)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (part.MessageType != WebSocketMessageType.Text)
                    throw new MarketDataSourceException("Kraken ticker stream ended or sent a non-text message.");
                await message.WriteAsync(buffer.AsMemory(0, part.Count), cancellationToken).ConfigureAwait(false);
                if (message.Length > 1_048_576)
                    throw new MarketDataSourceException("Kraken ticker message exceeded the safe size limit.");
            }
            while (!part.EndOfMessage);

            var tick = KrakenTickerV2Protocol.Parse(Encoding.UTF8.GetString(message.ToArray()), streamSymbol);
            if (tick is not null)
                yield return tick with { Symbol = requested };
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new MarketDataSourceException("Kraken closed the public ticker stream.");
    }
}

internal static class KrakenTickerV2Protocol
{
    internal static string Subscribe(string symbol) => JsonSerializer.Serialize(new
    {
        method = "subscribe",
        @params = new { channel = "ticker", symbol = new[] { symbol }, event_trigger = "trades", snapshot = true }
    });

    internal static PublicTradePrice? Parse(string payload, string subscribedSymbol)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                throw new MarketDataSourceException("Kraken rejected the public ticker subscription.");
            if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "ticker"
                || !root.TryGetProperty("type", out var type)
                || type.GetString() is not ("snapshot" or "update")
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("symbol", out var symbol)
                    || !string.Equals(symbol.GetString(), subscribedSymbol, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!item.TryGetProperty("last", out var last))
                    return null;
                if (!last.TryGetDecimal(out var price) || price <= 0m
                    || !item.TryGetProperty("timestamp", out var timestamp)
                    || !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var asOfUtc))
                    throw new MarketDataSourceException("Kraken ticker supplied an invalid price or timestamp.");
                return new(subscribedSymbol, price, asOfUtc, type.GetString() == "snapshot");
            }
            return null;
        }
        catch (JsonException exception)
        {
            throw new MarketDataSourceException("Kraken ticker supplied malformed JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new MarketDataSourceException("Kraken ticker supplied malformed fields.", exception);
        }
    }
}
