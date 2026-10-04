using System.Globalization;
using System.Text;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.Exchanges.Kraken.MarketData;

namespace Trading.ArchitectureTests;

public sealed class KrakenOhlcvtArchiveReaderTests
{
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_importedAt = s_start.AddHours(1);

    [Fact]
    public async Task ReadsClosedUtcDecimalCandlesIntoReproducibleContiguousSegments()
    {
        var csv = string.Join('\n',
            "time,open,high,low,close,volume,trades",
            Row(0, "10.1,11,10,10.5,2.25,3"),
            Row(1, "10.5,12,10.5,11.5,1.5,2"),
            Row(4, "12,13,11,12.5,1.25,1"));

        using var reader = new StringReader(csv);
        var archive = await KrakenOhlcvtArchiveReader.ReadAsync(
            reader, "BTC/EUR", CandleInterval.OneMinute, s_importedAt);

        Assert.Equal(3, archive.CandleCount);
        Assert.Equal([2, 1], archive.Segments.Select(segment => segment.Count));
        var gap = Assert.Single(archive.Gaps);
        Assert.Equal(s_start.AddMinutes(2), gap.FirstMissingOpenUtc);
        Assert.Equal(s_start.AddMinutes(4), gap.NextObservedOpenUtc);
        Assert.Equal(2, gap.MissingCandleCount);
        var first = archive.Segments[0][0];
        Assert.Equal(10.1m, first.Open);
        Assert.Equal(10.5m, first.Close);
        Assert.Equal(2.25m, first.Volume);
        Assert.Equal(TimeSpan.Zero, first.CloseTimeUtc.Offset);
        Assert.True(first.CanBeUsedForClosedCandleSignal);
        Assert.All(archive.Segments.SelectMany(segment => segment), candle =>
        {
            Assert.False(candle.IsDerived);
            Assert.Equal("BTC/EUR", candle.Symbol);
            Assert.Equal(CandleInterval.OneMinute, candle.Interval);
        });

        var segment = archive.Segments[0];
        var manifest = new HistoricalDataset(
            "fixture", "kraken-ohlcvt-archive", "BTC/EUR", "1m",
            segment[0].OpenTimeUtc, segment[^1].OpenTimeUtc, segment.Count,
            HistoricalCandleFingerprint.Compute(segment), "fixture-v1", s_importedAt);
        Assert.Equal(HistoricalCandleFingerprint.Compute(segment), manifest.ContentFingerprint);
        Assert.Equal(64, manifest.VersionIdentity.Length);

        using var bomReader = new StringReader('\uFEFF' + csv);
        var bomArchive = await KrakenOhlcvtArchiveReader.ReadAsync(
            bomReader, "BTC/EUR", CandleInterval.OneMinute, s_importedAt);
        Assert.Equal(archive.CandleCount, bomArchive.CandleCount);
        Assert.Equal(archive.NormalizedCsvFingerprint, bomArchive.NormalizedCsvFingerprint);

        using var changedTrades = new StringReader(csv.Replace(
            Row(4, "12,13,11,12.5,1.25,1"),
            Row(4, "12,13,11,12.5,1.25,2"), StringComparison.Ordinal));
        var revised = await KrakenOhlcvtArchiveReader.ReadAsync(
            changedTrades, "BTC/EUR", CandleInterval.OneMinute, s_importedAt);
        Assert.NotEqual(archive.NormalizedCsvFingerprint, revised.NormalizedCsvFingerprint);
        Assert.Equal(HistoricalCandleFingerprint.Compute(archive.Segments[1]),
            HistoricalCandleFingerprint.Compute(revised.Segments[1]));
    }

    [Fact]
    public async Task HeaderlessArchiveAndFourHourBoundaryUseTheRequestedNativeInterval()
    {
        using var reader = new StringReader(string.Join('\n',
            Row(0, "10,11,9,10,1,1"),
            Row(240, "10,11,9,10,1,1")));

        var archive = await KrakenOhlcvtArchiveReader.ReadAsync(
            reader, "ETH/USD", CandleInterval.FourHours, s_start.AddDays(1));

        Assert.Empty(archive.Gaps);
        Assert.Equal(2, Assert.Single(archive.Segments).Count);
        Assert.Equal(s_start.AddHours(4), archive.Segments[0][0].CloseTimeUtc);
    }

    [Theory]
    [InlineData("not-a-row")]
    [InlineData("10,11,9,10,1,1")]
    [InlineData("x,10,11,9,10,1,1")]
    [InlineData("0,10,11,9,10,1,1")]
    [InlineData("1700000000,10,9,8,10,1,1")]
    [InlineData("1700000000,10,11,8,12,1,1")]
    [InlineData("1700000000,10,11,8,10,0,1")]
    [InlineData("1700000000,10,11,8,10,1,0")]
    [InlineData("1700000000,10,11,8,10,1,1,extra")]
    [InlineData("9223372036854775807,10,11,8,10,1,1")]
    [InlineData(" ")]
    public async Task RejectsMalformedOrInvalidRowsWithoutReturningPartialSegments(string row)
    {
        using var reader = new StringReader($"{Row(0, "10,11,9,10,1,1")}\n{row}");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(reader, "BTC/EUR", CandleInterval.OneMinute, s_importedAt));
        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsDuplicateOutOfOrderMisalignedAndUnclosedEvidence()
    {
        foreach (var minute in new[] { 0, -1 })
        {
            using var reader = new StringReader(string.Join('\n',
                Row(0, "10,11,9,10,1,1"), Row(minute, "10,11,9,10,1,1")));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                KrakenOhlcvtArchiveReader.ReadAsync(reader, "BTC/EUR",
                    CandleInterval.OneMinute, s_importedAt));
        }

        using var misaligned = new StringReader($"{s_start.AddSeconds(30).ToUnixTimeSeconds()},10,11,9,10,1,1");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(misaligned, "BTC/EUR",
                CandleInterval.OneMinute, s_importedAt));

        using var unclosed = new StringReader(Row(0, "10,11,9,10,1,1"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(unclosed, "BTC/EUR",
                CandleInterval.OneMinute, s_start.AddMinutes(1)));

        using var lastSupportedTimestamp = new StringReader("253402300740,10,11,9,10,1,1");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(lastSupportedTimestamp, "BTC/EUR",
                CandleInterval.OneMinute, s_importedAt));
    }

    [Fact]
    public async Task RefusesUnsupportedIntervalsInvalidImportTimeAndCancellation()
    {
        using var unsupported = new StringReader(Row(0, "10,11,9,10,1,1"));
        await Assert.ThrowsAsync<Trading.MarketData.MarketDataIntervalNotSupportedException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(unsupported, "BTC/EUR",
                CandleInterval.TenMinutes, s_importedAt));

        using var nonUtc = new StringReader(Row(0, "10,11,9,10,1,1"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(nonUtc, "BTC/EUR",
                CandleInterval.OneMinute, s_importedAt.ToOffset(TimeSpan.FromHours(1))));

        using var futureImport = new StringReader(Row(0, "10,11,9,10,1,1"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(futureImport, "BTC/EUR",
                CandleInterval.OneMinute, DateTimeOffset.UtcNow.AddDays(1)));

        using var cancelled = new StringReader(Row(0, "10,11,9,10,1,1"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(cancelled, "BTC/EUR",
                CandleInterval.OneMinute, s_importedAt, cancellation.Token));
    }

    [Fact]
    public async Task RejectsInputBeyondTheDocumentedMemoryLimit()
    {
        var csv = new StringBuilder();
        for (var minute = 0; minute < KrakenOhlcvtArchiveReader.MaximumCandles; minute++)
            csv.Append(Row(minute, "10,11,9,10,1,1")).Append('\n');

        using var acceptedReader = new StringReader(csv.ToString());
        var accepted = await KrakenOhlcvtArchiveReader.ReadAsync(acceptedReader, "BTC/EUR",
            CandleInterval.OneMinute, s_start.AddMonths(6));
        Assert.Equal(KrakenOhlcvtArchiveReader.MaximumCandles, accepted.CandleCount);

        csv.Append(Row(KrakenOhlcvtArchiveReader.MaximumCandles, "10,11,9,10,1,1")).Append('\n');
        using var reader = new StringReader(csv.ToString());
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            KrakenOhlcvtArchiveReader.ReadAsync(reader, "BTC/EUR",
                CandleInterval.OneMinute, s_start.AddMonths(6)));
        Assert.Contains("100000 candle import limit", error.Message, StringComparison.Ordinal);
    }

    private static string Row(int minute, string values) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s_start.AddMinutes(minute).ToUnixTimeSeconds()},{values}");
}
