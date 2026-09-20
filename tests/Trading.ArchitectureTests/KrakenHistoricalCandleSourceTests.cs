using System.Globalization;
using System.Net;
using System.Text;
using Trading.Domain.Market;
using Trading.Exchanges.Kraken.MarketData;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class KrakenHistoricalCandleSourceTests
{
    // A committed bar, then the bar still forming. Kraken's 'last' is the open
    // time of the last *committed* bar, so it points at the first row here and
    // the second row is the one still forming. Verified against a live Kraken
    // response: for hourly bars at 10:43 UTC, 'last' was the 09:00 bar.
    private const string TwoBarPayload = """
        {"error":[],"result":{"XXBTZUSD":[
        [1700000000,"30100.1","30150.5","30090.0","30120.4","30110.0","12.5",42],
        [1700000060,"30120.4","30180.0","30115.0","30160.0","30150.0","3.25",11]
        ],"last":1700000000}}
        """;

    private static readonly long[] ExpectedAscendingOpenTimes =
        [1700000000L, 1700000060L, 1700000120L];

    [Fact]
    public void MapperReadsPricesAndVolumeAsDecimal()
    {
        var candles = KrakenOhlcMapper.Map(TwoBarPayload, "XBTUSD", CandleInterval.OneMinute);

        var first = candles[0];
        Assert.Equal(30100.1m, first.Open);
        Assert.Equal(30150.5m, first.High);
        Assert.Equal(30090.0m, first.Low);
        Assert.Equal(30120.4m, first.Close);
        Assert.Equal(12.5m, first.Volume);
    }

    [Fact]
    public void MapperTreatsTheBarStillFormingAsNotClosed()
    {
        var candles = KrakenOhlcMapper.Map(TwoBarPayload, "XBTUSD", CandleInterval.OneMinute);

        Assert.True(candles[0].IsClosed);

        // Kraken includes the forming bar in the same array as finished ones.
        // Treating it as closed would let a strategy act on a partial candle.
        Assert.False(candles[1].IsClosed);
    }

    [Fact]
    public void ABarStillFormingIsNotUsableForAClosedCandleSignal()
    {
        var candles = KrakenOhlcMapper.Map(TwoBarPayload, "XBTUSD", CandleInterval.OneMinute);

        Assert.True(candles[0].CanBeUsedForClosedCandleSignal);
        Assert.False(candles[1].CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public void TheBarWhoseOpenTimeEqualsTheMarkerIsClosed()
    {
        // Kraken's 'last' is the open time of the final committed bar, not the
        // first uncommitted one. Treating it as exclusive would mark the newest
        // finished bar as still forming and delay every closed-candle signal by
        // a full bar.
        const string payload = """
            {"error":[],"result":{"XXBTZUSD":[
            [1700000000,"1","1","1","1","1","1",1],
            [1700000060,"2","2","2","2","2","1",1],
            [1700000120,"3","3","3","3","3","1",1]
            ],"last":1700000060}}
            """;

        var candles = KrakenOhlcMapper.Map(payload, "XBTUSD", CandleInterval.OneMinute);

        Assert.True(candles[0].IsClosed);
        Assert.True(candles[1].IsClosed);
        Assert.False(candles[2].IsClosed);
    }

    [Fact]
    public void MapperUsesTheOpenTimeReportedByKraken()
    {
        var candles = KrakenOhlcMapper.Map(TwoBarPayload, "XBTUSD", CandleInterval.OneMinute);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), candles[0].OpenTimeUtc);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000060), candles[0].CloseTimeUtc);
        Assert.Equal(TimeSpan.Zero, candles[0].OpenTimeUtc.Offset);
    }

    [Fact]
    public void MapperMarksFetchedCandlesAsNotDerived()
    {
        var candles = KrakenOhlcMapper.Map(TwoBarPayload, "XBTUSD", CandleInterval.OneMinute);

        Assert.All(candles, candle => Assert.False(candle.IsDerived));
    }

    [Fact]
    public void MapperReturnsCandlesInAscendingOpenTimeOrder()
    {
        const string outOfOrder = """
            {"error":[],"result":{"XXBTZUSD":[
            [1700000120,"3","3","3","3","3","1",1],
            [1700000000,"1","1","1","1","1","1",1],
            [1700000060,"2","2","2","2","2","1",1]
            ],"last":1700000180}}
            """;

        var candles = KrakenOhlcMapper.Map(outOfOrder, "XBTUSD", CandleInterval.OneMinute);

        Assert.Equal(
            ExpectedAscendingOpenTimes,
            candles.Select(c => c.OpenTimeUtc.ToUnixTimeSeconds()));
    }

    [Fact]
    public void MapperKeepsDuplicateBarsSoTheyCanBeDetected()
    {
        const string duplicated = """
            {"error":[],"result":{"XXBTZUSD":[
            [1700000000,"1","1","1","1","1","1",1],
            [1700000000,"1","1","1","1","1","1",1]
            ],"last":1700000060}}
            """;

        var candles = KrakenOhlcMapper.Map(duplicated, "XBTUSD", CandleInterval.OneMinute);

        // Dropping the duplicate here would hide a venue fault. The quality
        // evaluator is the component that reports it.
        Assert.Equal(2, candles.Count);

        var issues = CandleQualityEvaluator.Evaluate(
            candles[1],
            candles[0],
            DateTimeOffset.FromUnixTimeSeconds(1700000060),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(5));

        Assert.Contains(DataQualityIssue.Duplicate, issues);
    }

    [Fact]
    public void MapperRejectsAResponseCarryingAVenueError()
    {
        const string failed = """{"error":["EQuery:Unknown asset pair"],"result":{}}""";

        var exception = Assert.Throws<MarketDataSourceException>(
            () => KrakenOhlcMapper.Map(failed, "NOPE", CandleInterval.OneMinute));

        // Kraken answers HTTP 200 on failure, so an unreported error would look
        // like a market with no trading activity.
        Assert.Contains("Unknown asset pair", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapperRejectsAResponseWithoutTheCommittedBarMarker()
    {
        const string noLast = """
            {"error":[],"result":{"XXBTZUSD":[[1700000000,"1","1","1","1","1","1",1]]}}
            """;

        Assert.Throws<MarketDataSourceException>(
            () => KrakenOhlcMapper.Map(noLast, "XBTUSD", CandleInterval.OneMinute));
    }

    [Fact]
    public void MapperRejectsMalformedPayloads()
    {
        Assert.Throws<MarketDataSourceException>(
            () => KrakenOhlcMapper.Map("not json", "XBTUSD", CandleInterval.OneMinute));

        Assert.Throws<MarketDataSourceException>(
            () => KrakenOhlcMapper.Map(string.Empty, "XBTUSD", CandleInterval.OneMinute));

        Assert.Throws<MarketDataSourceException>(
            () => KrakenOhlcMapper.Map("""{"error":[],"result":{"P":[[1700000000,"1"]],"last":1}}""", "XBTUSD", CandleInterval.OneMinute));
    }

    [Fact]
    public void MapperFindsTheSeriesEvenWhenKrakenRenamesThePair()
    {
        // "XBTUSD" is requested but Kraken keys the series "XXBTZUSD".
        var candles = KrakenOhlcMapper.Map(TwoBarPayload, "XBTUSD", CandleInterval.OneMinute);

        Assert.NotEmpty(candles);
        Assert.All(candles, candle => Assert.Equal("XBTUSD", candle.Symbol));
    }

    [Theory]
    [InlineData(CandleInterval.OneMinute, 1)]
    [InlineData(CandleInterval.FiveMinutes, 5)]
    [InlineData(CandleInterval.FifteenMinutes, 15)]
    [InlineData(CandleInterval.ThirtyMinutes, 30)]
    [InlineData(CandleInterval.OneHour, 60)]
    [InlineData(CandleInterval.FourHours, 240)]
    [InlineData(CandleInterval.OneDay, 1440)]
    public void NativeIntervalsMapToKrakenMinutes(CandleInterval interval, int expected)
    {
        Assert.Equal(expected, KrakenIntervalMap.ToKrakenMinutes(interval));
        Assert.True(KrakenIntervalMap.IsNativelySupported(interval));
    }

    [Fact]
    public async Task TenMinuteCandlesAreRefusedRatherThanSubstituted()
    {
        using var handler = new StubHandler(TwoBarPayload);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.com") };
        var source = new KrakenHistoricalCandleSource(client);

        // Kraken has no ten-minute bar. Answering with fifteen-minute data
        // would be a materially different candle than the caller asked for.
        await Assert.ThrowsAsync<MarketDataIntervalNotSupportedException>(
            () => source.FetchAsync("XBTUSD", CandleInterval.TenMinutes, DateTimeOffset.UnixEpoch));

        Assert.False(KrakenIntervalMap.IsNativelySupported(CandleInterval.TenMinutes));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void RequestUriCarriesThePairIntervalAndStart()
    {
        var uri = KrakenHistoricalCandleSource.BuildRequestUri("XBTUSD", 5, DateTimeOffset.FromUnixTimeSeconds(1700000000));

        Assert.Equal(
            "/0/public/OHLC?pair=XBTUSD&interval=5&since=1700000000",
            uri.ToString());
    }

    [Fact]
    public void RequestUriClampsAPreEpochStartToZero()
    {
        var uri = KrakenHistoricalCandleSource.BuildRequestUri("XBTUSD", 1, DateTimeOffset.MinValue);

        Assert.EndsWith("since=0", uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RequestUriIsBuiltWithTheInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // A culture using a comma decimal separator must not reshape the
            // numbers in the query string.
            CultureInfo.CurrentCulture = new CultureInfo("nb-NO");
            var uri = KrakenHistoricalCandleSource.BuildRequestUri("XBTUSD", 240, DateTimeOffset.FromUnixTimeSeconds(1700000000));

            Assert.Equal(
                "/0/public/OHLC?pair=XBTUSD&interval=240&since=1700000000",
                uri.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task FetchReturnsNormalizedCandles()
    {
        using var handler = new StubHandler(TwoBarPayload);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.com") };
        var source = new KrakenHistoricalCandleSource(client);

        var candles = await source.FetchAsync("XBTUSD", CandleInterval.OneMinute, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, candles.Count);
        Assert.Equal(CandleInterval.OneMinute, candles[0].Interval);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchSendsNoCredentialBecauseTheEndpointIsPublic()
    {
        using var handler = new StubHandler(TwoBarPayload);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.com") };
        var source = new KrakenHistoricalCandleSource(client);

        await source.FetchAsync("XBTUSD", CandleInterval.OneMinute, DateTimeOffset.UnixEpoch);

        // Price history needs no permission on a user's account and must never
        // consume one.
        Assert.NotNull(handler.LastRequest);
        Assert.False(handler.LastRequest!.Headers.Contains("API-Key"));
        Assert.False(handler.LastRequest.Headers.Contains("API-Sign"));
        Assert.Null(handler.LastRequest.Headers.Authorization);
    }

    [Fact]
    public async Task FetchRequiresASymbol()
    {
        using var handler = new StubHandler(TwoBarPayload);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.com") };
        var source = new KrakenHistoricalCandleSource(client);

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.FetchAsync("  ", CandleInterval.OneMinute, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task FetchSurfacesAnUnreachableVenueAsAMarketDataFailure()
    {
        using var handler = new ThrowingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.com") };
        var source = new KrakenHistoricalCandleSource(client);

        await Assert.ThrowsAsync<MarketDataSourceException>(
            () => source.FetchAsync("XBTUSD", CandleInterval.OneMinute, DateTimeOffset.UnixEpoch));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _payload;

        public StubHandler(string payload) => _payload = payload;

        public int CallCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
