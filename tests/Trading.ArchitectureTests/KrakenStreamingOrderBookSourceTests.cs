using System.Text.Json;
using Trading.Exchanges.Kraken.MarketData;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class KrakenStreamingOrderBookSourceTests
{
    private const string Snapshot = """
        {"channel":"book","type":"snapshot","data":[{"symbol":"SOL/USD",
        "timestamp":"2026-09-20T10:00:00.000000Z",
        "bids":[{"price":99.0,"qty":1.25}],"asks":[{"price":101.0,"qty":2.50}],
        "checksum":3291031825}]}
        """;

    [Fact]
    public void SubscribeMessageUsesThePublicV2BookProtocol()
    {
        using var document = JsonDocument.Parse(
            KrakenOrderBookV2Protocol.BuildSubscribeMessage("SOL/USD", 10));

        var root = document.RootElement;
        Assert.Equal("subscribe", root.GetProperty("method").GetString());
        var parameters = root.GetProperty("params");
        Assert.Equal("book", parameters.GetProperty("channel").GetString());
        Assert.Equal(10, parameters.GetProperty("depth").GetInt32());
        Assert.True(parameters.GetProperty("snapshot").GetBoolean());
        Assert.Equal("SOL/USD", Assert.Single(parameters.GetProperty("symbol").EnumerateArray()).GetString());
        Assert.DoesNotContain("token", root.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key", root.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("wss", KrakenStreamingOrderBookSource.Endpoint.Scheme);
    }

    [Theory]
    [InlineData("XBT/USD", "BTC/USD")]
    [InlineData("XDG/EUR", "DOGE/EUR")]
    public void LegacyRestNamesSubscribeToSupportedBookSymbols(string requested, string streamName)
    {
        using var message = JsonDocument.Parse(KrakenOrderBookV2Protocol.BuildSubscribeMessage(
            KrakenV2SymbolNames.ForPublicStream(requested), 10));
        Assert.Equal(streamName, Assert.Single(
            message.RootElement.GetProperty("params").GetProperty("symbol").EnumerateArray()).GetString());
    }

    [Fact]
    public void PublicSocketDoesNotAttachCredentialsOrClientCertificates()
    {
        using var socket = KrakenStreamingOrderBookSource.CreatePublicSocket();

        Assert.Null(socket.Options.Credentials);
        Assert.Empty(socket.Options.ClientCertificates);
    }

    [Fact]
    public void SnapshotVerifiesKrakenChecksumAndPreservesDecimalLevels()
    {
        var state = new KrakenOrderBookV2Protocol.BookState("SOL/USD", 10);

        var snapshot = KrakenOrderBookV2Protocol.Apply(Snapshot, state);

        Assert.True(state.HasSnapshot);
        Assert.True(snapshot!.IsSynchronized);
        Assert.Equal(3291031825u, snapshot.Checksum);
        Assert.Equal(99.0m, Assert.Single(snapshot.Bids).Price);
        Assert.Equal(1.25m, Assert.Single(snapshot.Bids).Quantity);
        Assert.Equal(101.0m, Assert.Single(snapshot.Asks).Price);
        Assert.Equal(2.50m, Assert.Single(snapshot.Asks).Quantity);
    }

    [Fact]
    public void SnapshotVerifiesChecksumForLiveKrakenDecimalLevels()
    {
        const string payload = """
            {"channel":"book","type":"snapshot","data":[{"symbol":"SOL/USD",
            "timestamp":"2026-10-02T21:32:21.570950Z",
            "bids":[
              {"price":117.88,"qty":0.59052327},{"price":117.87,"qty":84.83380412},
              {"price":117.86,"qty":44.65577787},{"price":117.85,"qty":109.77771452},
              {"price":117.84,"qty":157.16144745},{"price":117.83,"qty":556.29908373},
              {"price":117.82,"qty":236.49213330},{"price":117.81,"qty":454.19452360},
              {"price":117.80,"qty":385.67680875},{"price":117.79,"qty":1222.39148977}],
            "asks":[
              {"price":117.89,"qty":127.24086668},{"price":117.90,"qty":132.09414976},
              {"price":117.91,"qty":157.27089736},{"price":117.92,"qty":632.31333563},
              {"price":117.93,"qty":600.40294890},{"price":117.94,"qty":729.70875304},
              {"price":117.95,"qty":641.11480306},{"price":117.96,"qty":621.80068998},
              {"price":117.97,"qty":818.64420868},{"price":117.98,"qty":2205.80390195}],
            "checksum":1075898951}]}
            """;
        var state = new KrakenOrderBookV2Protocol.BookState("SOL/USD", 10);

        var snapshot = KrakenOrderBookV2Protocol.Apply(payload, state);

        Assert.True(snapshot!.IsSynchronized);
        Assert.Equal(1075898951u, snapshot.Checksum);
        Assert.Equal(117.88m, snapshot.Bids[0].Price);
        Assert.Equal(117.89m, snapshot.Asks[0].Price);
    }

    [Fact]
    public void SnapshotAndIncrementalUpdatesMustPassChecksum()
    {
        var state = new KrakenOrderBookV2Protocol.BookState("SOL/USD", 10);
        KrakenOrderBookV2Protocol.Apply(Snapshot, state);

        var invalidUpdate = """
            {"channel":"book","type":"update","data":[{"symbol":"SOL/USD",
            "bids":[{"price":99.0,"qty":1.50}],"checksum":1}]}
            """;

        Assert.Throws<MarketDataSourceException>(() =>
            KrakenOrderBookV2Protocol.Apply(invalidUpdate, state));
    }

    [Fact]
    public void IncrementalUpdateAppliesOnlyAfterTheUpdatedBookChecksumMatches()
    {
        var state = new KrakenOrderBookV2Protocol.BookState("SOL/USD", 10);
        KrakenOrderBookV2Protocol.Apply(Snapshot, state);
        var update = """
            {"channel":"book","type":"update","data":[{"symbol":"SOL/USD",
            "timestamp":"2026-09-20T10:00:01.000000Z",
            "bids":[{"price":99.0,"qty":1.50}],"checksum":4211231577}]}
            """;

        var result = KrakenOrderBookV2Protocol.Apply(update, state);

        Assert.Equal(1.50m, Assert.Single(result!.Bids).Quantity);
        Assert.Equal(4211231577u, result.Checksum);
    }

    [Fact]
    public void IncrementalMessagesAreIgnoredUntilTheSnapshotArrives()
    {
        var state = new KrakenOrderBookV2Protocol.BookState("SOL/USD", 10);

        var update = KrakenOrderBookV2Protocol.Apply(
            """{"channel":"book","type":"update","data":[{"symbol":"SOL/USD","bids":[{"price":99,"qty":1}],"checksum":1}]}""",
            state);

        Assert.Null(update);
        Assert.Empty(state.Bids);
    }

    [Fact]
    public void NonBookControlMessagesDoNotProduceSnapshots()
    {
        var state = new KrakenOrderBookV2Protocol.BookState("SOL/USD", 10);

        Assert.Null(KrakenOrderBookV2Protocol.Apply(
            """{"method":"subscribe","success":true}""",
            state));
    }
}
