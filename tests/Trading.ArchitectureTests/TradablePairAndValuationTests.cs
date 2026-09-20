using System.Net;
using System.Text;
using Trading.Application.Execution;
using Trading.Domain.Market;
using Trading.Domain.Positions;
using Trading.Exchanges.Kraken.MarketData;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class TradablePairAndValuationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StrategyId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string PairPayload = """
    {"error":[],"result":{
      "XXBTZUSD":{"altname":"XBTUSD","wsname":"XBT/USD","base":"XXBT","quote":"ZUSD","status":"online","lot_decimals":8,"ordermin":"0.00005","tick_size":"0.1"},
      "XETHZUSD":{"altname":"ETHUSD","wsname":"ETH/USD","base":"XETH","quote":"ZUSD","status":"online","lot_decimals":8,"ordermin":"0.002","tick_size":"0.01"}
    }}
    """;

    [Fact]
    public void PairFiltersAreReadAsDecimalsFromTheVenueStrings()
    {
        var pairs = KrakenAssetPairMapper.Map(PairPayload);

        var bitcoin = pairs.Single(pair => pair.Symbol == "XBTUSD");

        Assert.Equal("XBT/USD", bitcoin.DisplayName);
        Assert.Equal(0.00005m, bitcoin.MinimumQuantity);
        Assert.Equal(0.1m, bitcoin.PriceTick);
        Assert.Equal(0.00000001m, bitcoin.QuantityStep);
        Assert.True(bitcoin.IsActive);
    }

    [Fact]
    public void APairMissingItsOrderFiltersIsSkippedRatherThanDefaulted()
    {
        // A guessed filter would let the platform submit an order the venue
        // rejects, or refuse one it would have accepted.
        const string payload = """
        {"error":[],"result":{
          "GOOD":{"altname":"GOODUSD","wsname":"GOOD/USD","base":"GOOD","quote":"ZUSD","lot_decimals":8,"ordermin":"1","tick_size":"0.01"},
          "NOFILTER":{"altname":"BADUSD","wsname":"BAD/USD","base":"BAD","quote":"ZUSD","lot_decimals":8}
        }}
        """;

        var pairs = KrakenAssetPairMapper.Map(payload);

        Assert.Equal("GOODUSD", Assert.Single(pairs).Symbol);
    }

    [Fact]
    public void ADelistedPairIsReportedAsInactive()
    {
        const string payload = """
        {"error":[],"result":{"OLD":{"altname":"OLDUSD","wsname":"OLD/USD","base":"OLD","quote":"ZUSD","status":"delisted","lot_decimals":8,"ordermin":"1","tick_size":"0.01"}}}
        """;

        Assert.False(Assert.Single(KrakenAssetPairMapper.Map(payload)).IsActive);
    }

    [Fact]
    public void AVenueErrorIsRaisedRatherThanReturnedAsAnEmptyPairList()
    {
        // "The venue trades nothing" and "the request failed" lead to opposite
        // conclusions, so they must not share a representation.
        var exception = Assert.Throws<MarketDataSourceException>(
            () => KrakenAssetPairMapper.Map("""{"error":["EGeneral:Invalid arguments"],"result":{}}"""));

        Assert.Contains("Invalid arguments", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedPayloadIsRaisedRatherThanReturnedAsNoPairs()
    {
        Assert.Throws<MarketDataSourceException>(
            () => KrakenAssetPairMapper.Map("""{"error":[],"result":{}}"""));
    }

    [Fact]
    public async Task ThePairListIsFetchedOnceAndServedFromCache()
    {
        using var handler = new CountingHandler(PairPayload);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        using var source = new KrakenTradablePairSource(client, new FixedTimeProvider(Now));

        await source.ListAsync();
        await source.ListAsync();
        await source.ListAsync();

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ThePairRequestCarriesNoCredentialBecauseTheEndpointIsPublic()
    {
        using var handler = new CountingHandler(PairPayload);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.kraken.test") };
        using var source = new KrakenTradablePairSource(client, new FixedTimeProvider(Now));

        await source.ListAsync();

        // Listing what a venue trades needs no permission on a user's account
        // and must never consume one.
        Assert.NotNull(handler.LastRequest);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
        Assert.DoesNotContain(handler.LastRequest.Headers, header =>
            header.Key.Contains("API", StringComparison.OrdinalIgnoreCase)
            || header.Key.Contains("Sign", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task APositionIsValuedAtTheLastClosedCandleNotTheFormingBar()
    {
        var harness = Harness.WithLongPosition(entryPrice: 100m, quantity: 2m);

        var valued = Assert.Single(await harness.Service.ValueOpenPositionsAsync(UserId));

        // The fixture's forming bar prints 500. Valuing against it would show a
        // profit at a price that has not settled.
        Assert.Equal(110m, valued.MarkPrice);
        Assert.Equal(20m, valued.UnrealisedPnl);
        Assert.Equal(10m, valued.UnrealisedPercent);
        Assert.False(valued.PriceIsStale);
    }

    [Fact]
    public async Task AShortPositionGainsWhenPriceFalls()
    {
        var harness = Harness.WithPosition(PositionDirection.DirectionShort, entryPrice: 120m, quantity: 2m);

        var valued = Assert.Single(await harness.Service.ValueOpenPositionsAsync(UserId));

        Assert.Equal(20m, valued.UnrealisedPnl);
    }

    [Fact]
    public async Task AStalePriceIsMarkedRatherThanPresentedAsCurrent()
    {
        var harness = Harness.WithLongPosition(
            entryPrice: 100m,
            quantity: 1m,
            candles: [ClosedCandle(Now.AddHours(-4), 110m)]);

        var valued = Assert.Single(await harness.Service.ValueOpenPositionsAsync(UserId));

        Assert.True(valued.PriceIsStale);
    }

    [Fact]
    public async Task AnUnpricedPositionReportsAnUnknownResultRatherThanZero()
    {
        var harness = Harness.WithLongPosition(entryPrice: 100m, quantity: 1m, candles: []);

        var valued = Assert.Single(await harness.Service.ValueOpenPositionsAsync(UserId));

        // A position with no price has an unknown result, which is not the
        // same as a result of zero.
        Assert.Null(valued.MarkPrice);
        Assert.Null(valued.UnrealisedPnl);
        Assert.Null(valued.UnrealisedPercent);
        Assert.NotNull(valued.PriceUnavailableReason);
    }

    [Fact]
    public async Task AVenueFailureDoesNotValueThePositionAtZero()
    {
        var harness = Harness.WithLongPosition(entryPrice: 100m, quantity: 1m, failWith: "Kraken is unavailable.");

        var valued = Assert.Single(await harness.Service.ValueOpenPositionsAsync(UserId));

        Assert.Null(valued.UnrealisedPnl);
        Assert.Contains("unavailable", valued.PriceUnavailableReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneUsersPositionsAreNotValuedForAnother()
    {
        var harness = Harness.WithLongPosition(entryPrice: 100m, quantity: 1m);

        var others = await harness.Service.ValueOpenPositionsAsync(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        Assert.Empty(others);
    }

    [Fact]
    public void ReducingAPositionRecomputesTheUnrealisedResultForTheRemainingSize()
    {
        var position = new Position(
            Guid.NewGuid(), UserId, StrategyId, "XBTUSD",
            PositionDirection.DirectionLong, quantity: 4m, entryPrice: 100m, markPrice: 100m, openedAtUtc: Now);

        position.UpdateMarkPrice(110m);
        Assert.Equal(40m, position.UnrealizedPnl);

        position.Reduce(3m);

        // Leaving this untouched would report the exposure of a size no longer
        // held, overstating both gains and losses after every partial exit.
        Assert.Equal(1m, position.Quantity);
        Assert.Equal(10m, position.UnrealizedPnl);
    }

    [Fact]
    public void AFullyClosedPositionHasNoUnrealisedResult()
    {
        var position = new Position(
            Guid.NewGuid(), UserId, StrategyId, "XBTUSD",
            PositionDirection.DirectionLong, quantity: 2m, entryPrice: 100m, markPrice: 100m, openedAtUtc: Now);

        position.UpdateMarkPrice(150m);
        position.Reduce(2m);

        Assert.Equal(PositionStatus.Flat, position.Status);
        Assert.Equal(0m, position.UnrealizedPnl);
    }

    [Fact]
    public void ReducingAPositionAdvancesTheConcurrencyToken()
    {
        var position = new Position(
            Guid.NewGuid(), UserId, StrategyId, "XBTUSD",
            PositionDirection.DirectionLong, quantity: 2m, entryPrice: 100m, markPrice: 100m, openedAtUtc: Now);

        var before = position.Version;
        position.Reduce(1m);

        // Otherwise a concurrent writer can overwrite the reduction.
        Assert.True(position.Version > before);
    }

    private static Candle ClosedCandle(DateTimeOffset closeTime, decimal close) =>
        new("XBTUSD", CandleInterval.OneMinute, closeTime.AddMinutes(-1), closeTime,
            close, close, close, close, volume: 1m, isClosed: true, isDerived: false);

    private static Candle FormingCandle(DateTimeOffset closeTime, decimal close) =>
        new("XBTUSD", CandleInterval.OneMinute, closeTime.AddMinutes(-1), closeTime,
            close, close, close, close, volume: 1m, isClosed: false, isDerived: false);

    private sealed class Harness
    {
        private Harness(IReadOnlyList<Candle> candles, string? failWith, Position position)
        {
            Positions = new InMemoryPositionRepository();
            Positions.AddAsync(position, CancellationToken.None).GetAwaiter().GetResult();

            Service = new PositionValuationService(
                new StubCandleSource(candles, failWith),
                Positions,
                new FixedTimeProvider(Now));
        }

        public InMemoryPositionRepository Positions { get; }

        public PositionValuationService Service { get; }

        public static Harness WithLongPosition(
            decimal entryPrice,
            decimal quantity,
            IReadOnlyList<Candle>? candles = null,
            string? failWith = null) =>
            WithPosition(PositionDirection.DirectionLong, entryPrice, quantity, candles, failWith);

        public static Harness WithPosition(
            PositionDirection direction,
            decimal entryPrice,
            decimal quantity,
            IReadOnlyList<Candle>? candles = null,
            string? failWith = null) =>
            new(
                candles ??
                [
                    ClosedCandle(Now.AddMinutes(-2), 105m),
                    ClosedCandle(Now.AddMinutes(-1), 110m),
                    FormingCandle(Now.AddMinutes(1), 500m),
                ],
                failWith,
                new Position(
                    Guid.NewGuid(), UserId, StrategyId, "XBTUSD",
                    direction, quantity, entryPrice, entryPrice, Now.AddHours(-1)));
    }

    private sealed class StubCandleSource : IHistoricalCandleSource
    {
        private readonly IReadOnlyList<Candle> _candles;
        private readonly string? _failWith;

        public StubCandleSource(IReadOnlyList<Candle> candles, string? failWith)
        {
            _candles = candles;
            _failWith = failWith;
        }

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) =>
            _failWith is null
                ? Task.FromResult(_candles)
                : Task.FromException<IReadOnlyList<Candle>>(new MarketDataSourceException(_failWith));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _payload;

        public CountingHandler(string payload) => _payload = payload;

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
                Content = new StringContent(_payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
