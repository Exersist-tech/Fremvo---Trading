using System.Text.Json;
using Trading.Domain.Market;
using Trading.Exchanges.Kraken.MarketData;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class KrakenStreamingCandleSourceTests
{
    private static readonly string[] s_symbols = ["btc/usd", "ETH/USD"];
    private static readonly string[] s_expectedSymbols = ["BTC/USD", "ETH/USD"];

    private const string Update = """
        {"channel":"ohlc","type":"update","data":[{"symbol":"BTC/USD","interval":1,
        "interval_begin":"2026-09-20T10:00:00.000000Z","open":"101.123456789012",
        "high":"105.123456789012","low":"99.123456789012","close":"103.123456789012",
        "volume":"456.123456789012","trades":42}]}
        """;

    [Fact]
    public void SubscribeMessageUsesOnlyThePublicV2OhlcProtocol()
    {
        using var document = JsonDocument.Parse(
            KrakenOhlcV2Protocol.BuildSubscribeMessage(s_symbols, 1));

        var root = document.RootElement;
        Assert.Equal("subscribe", root.GetProperty("method").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal("ohlc", parameters.GetProperty("channel").GetString());
        Assert.True(parameters.GetProperty("snapshot").GetBoolean());
        Assert.Equal(1, parameters.GetProperty("interval").GetInt32());
        Assert.Equal(s_expectedSymbols,
            parameters.GetProperty("symbol").EnumerateArray().Select(value => value.GetString()));
        Assert.False(root.ToString().Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.False(root.ToString().Contains("key", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("wss", KrakenStreamingCandleSource.Endpoint.Scheme);
    }

    [Fact]
    public void PublicSocketDoesNotAttachCredentialsOrClientCertificates()
    {
        using var socket = KrakenStreamingCandleSource.CreatePublicSocket();

        Assert.Null(socket.Options.Credentials);
        Assert.Empty(socket.Options.ClientCertificates);
    }

    [Fact]
    public void MapperParsesDecimalStringsAndMarksV2UpdatesAsForming()
    {
        var candle = Assert.Single(KrakenOhlcV2Protocol.Map(Update));

        Assert.Equal("BTC/USD", candle.Symbol);
        Assert.Equal(CandleInterval.OneMinute, candle.Interval);
        Assert.Equal(101.123456789012m, candle.Open);
        Assert.Equal(456.123456789012m, candle.Volume);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), candle.OpenTimeUtc);
        Assert.False(candle.IsClosed);
        Assert.False(candle.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public void FinalizingAFormingUpdateRemovesOnlyTheIncompleteFlag()
    {
        var forming = Assert.Single(KrakenOhlcV2Protocol.Map(Update));

        var closed = KrakenStreamingCandleSource.AsClosed(forming);

        Assert.True(closed.IsClosed);
        Assert.True(closed.CanBeUsedForClosedCandleSignal);
        Assert.DoesNotContain(DataQualityIssue.Incomplete, closed.QualityFlags);
    }

    [Fact]
    public void MapperRejectsMalformedOhlcMessages()
    {
        Assert.Throws<MarketDataSourceException>(() => KrakenOhlcV2Protocol.Map("{not json"));
        Assert.Throws<MarketDataSourceException>(() => KrakenOhlcV2Protocol.Map(
            """{"channel":"ohlc","data":[{"symbol":"BTC/USD","interval":1}]}"""));
    }

    [Fact]
    public void NonOhlcControlMessagesDoNotProduceCandles()
    {
        Assert.Empty(KrakenOhlcV2Protocol.Map("""{"method":"subscribe","success":true}"""));
    }
}
