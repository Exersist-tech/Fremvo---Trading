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

    [Theory]
    [InlineData("XBT/USD", "BTC/USD")]
    [InlineData("XDG/EUR", "DOGE/EUR")]
    [InlineData("SOL/USD", "SOL/USD")]
    public void KrakenRestDisplayNamesMapToPublicV2StreamNames(string restName, string streamName)
    {
        Assert.Equal(streamName, KrakenV2SymbolNames.ForPublicStream(restName));
        var forming = Assert.Single(KrakenOhlcV2Protocol.Map(Update));
        var requested = KrakenStreamingCandleSource.AsRequested(
            KrakenStreamingCandleSource.AsClosed(forming), restName);
        Assert.Equal(restName, requested.Symbol);
        Assert.True(requested.CanBeUsedForClosedCandleSignal);
        Assert.Equal(forming.Close, requested.Close);
        Assert.Equal(forming.Volume, requested.Volume);
    }

    [Fact]
    public void EachPublicSocketSubscribesToOnlyOneIntervalPerSymbol()
    {
        var subscriptions = new[]
        {
            new CandleSubscription("XBT/USD", CandleInterval.FiveMinutes),
            new CandleSubscription("BTC/USD", CandleInterval.FiveMinutes),
            new CandleSubscription("XBT/USD", CandleInterval.OneHour),
            new CandleSubscription("DOGE/EUR", CandleInterval.OneHour)
        };
        var groups = KrakenStreamingCandleSource.SplitByInterval(subscriptions);

        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Single(group.Select(item => item.Interval).Distinct()));
        Assert.Equal(2, groups.Single(group => group[0].Interval == CandleInterval.FiveMinutes).Length);
        Assert.Equal(2, groups.Single(group => group[0].Interval == CandleInterval.OneHour).Length);
    }

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
    public void SnapshotTracksOnlyNewestFormingCandleAndIgnoresDelayedOlderUpdates()
    {
        var baseCandle = Assert.Single(KrakenOhlcV2Protocol.Map(Update));
        var latest = FormingAt(baseCandle, baseCandle.OpenTimeUtc.AddMinutes(2));
        var snapshot = KrakenOhlcV2Protocol.LatestSnapshotCandles(
            [latest, baseCandle, FormingAt(baseCandle, baseCandle.OpenTimeUtc.AddMinutes(1))]);
        Assert.Equal(latest.OpenTimeUtc, Assert.Single(snapshot).OpenTimeUtc);
        var parsed = KrakenOhlcV2Protocol.Parse(
            """{"channel":"ohlc","type":"snapshot","data":[]}""");
        Assert.True(parsed.IsSnapshot);

        var active = new Dictionary<CandleSubscription, Candle>();
        Assert.Null(KrakenStreamingCandleSource.Advance(active, latest));
        Assert.Null(KrakenStreamingCandleSource.Advance(active, baseCandle));
        Assert.Equal(latest.OpenTimeUtc, Assert.Single(active).Value.OpenTimeUtc);
        var closed = KrakenStreamingCandleSource.Advance(
            active, FormingAt(baseCandle, latest.OpenTimeUtc.AddMinutes(1)));
        Assert.Equal(latest.OpenTimeUtc, closed!.OpenTimeUtc);
        Assert.True(closed.CanBeUsedForClosedCandleSignal);
    }

    private static Candle FormingAt(Candle candle, DateTimeOffset open) => new(
        candle.Symbol, candle.Interval, open, open.AddMinutes((int)candle.Interval),
        candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, false, false);

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
        var error = Assert.Throws<MarketDataSourceException>(() =>
            KrakenOhlcV2Protocol.Map(
                """{"method":"subscribe","success":false,"error":"Currency pair not supported XBT/USD"}"""));
        Assert.Contains("XBT/USD", error.Message, StringComparison.Ordinal);
    }
}
