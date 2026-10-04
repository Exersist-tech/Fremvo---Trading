using System.Text.Json;
using Trading.Exchanges.Kraken.MarketData;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class KrakenStreamingTickerSourceTests
{
    [Fact]
    public void SubscribeRequestsTradeTriggeredPublicTicksAndSupportsKrakenSymbolAliases()
    {
        using var message = JsonDocument.Parse(
            KrakenTickerV2Protocol.Subscribe(KrakenV2SymbolNames.ForPublicStream("XBT/USD")));
        Assert.Equal("ticker", message.RootElement.GetProperty("params").GetProperty("channel").GetString());
        Assert.Equal("BTC/USD", message.RootElement.GetProperty("params").GetProperty("symbol")[0].GetString());
        Assert.Equal("trades", message.RootElement.GetProperty("params").GetProperty("event_trigger").GetString());
    }

    [Fact]
    public void ParsesLastTradeAsDecimalAndPreservesSnapshotProvenance()
    {
        const string update = """
            {"channel":"ticker","type":"update","data":[
              {"symbol":"BTC/USD","last":84944.12345678,"timestamp":"2026-10-04T07:32:00.123456Z"}]}
            """;
        var tick = KrakenTickerV2Protocol.Parse(update, "BTC/USD");
        Assert.NotNull(tick);
        Assert.Equal(84944.12345678m, tick.Price);
        Assert.Equal(TimeSpan.Zero, tick.AsOfUtc.Offset);
        Assert.False(tick.IsSnapshot);
        Assert.True(KrakenTickerV2Protocol.Parse(update.Replace("\"update\"", "\"snapshot\"", StringComparison.Ordinal), "BTC/USD")!.IsSnapshot);
        Assert.Null(KrakenTickerV2Protocol.Parse(update, "ETH/USD"));
        Assert.Null(KrakenTickerV2Protocol.Parse("""{"channel":"heartbeat"}""", "BTC/USD"));
    }

    [Theory]
    [InlineData("""{"success":false,"error":"unknown symbol"}""")]
    [InlineData("""{"channel":"ticker","type":"update","data":[{"symbol":"BTC/USD","last":0,"timestamp":"2026-10-04T07:32:00Z"}]}""")]
    [InlineData("""{"channel":"ticker","type":"update","data":[{"symbol":"BTC/USD","last":1,"timestamp":"invalid"}]}""")]
    [InlineData("not-json")]
    public void RejectsFailedSubscriptionsAndInvalidTicks(string payload) =>
        Assert.Throws<MarketDataSourceException>(() => KrakenTickerV2Protocol.Parse(payload, "BTC/USD"));
}
