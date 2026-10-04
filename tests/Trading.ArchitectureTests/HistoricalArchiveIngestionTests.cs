using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Backtesting;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.Exchanges.Kraken.MarketData;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Backtesting;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class HistoricalArchiveIngestionTests
{
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_importTime = s_start.AddDays(1);

    [Fact]
    public async Task StoresVerifiedPayloadBeforeDiscoverableManifestAndReusesAnExistingVersion()
    {
        await using var context = CreateContext();
        var manifests = new EfHistoricalDatasetRepository(context);
        var payloads = new FakePayloadStore(manifests);
        var service = new HistoricalArchiveIngestionService(payloads, manifests);
        var candles = Candles(10m, 11m, 12m);
        var sourceVersion = new string('A', 64);

        var first = await service.ImportSegmentAsync(candles, "kraken-ohlcvt-csv", sourceVersion, s_importTime);
        var repeat = await service.ImportSegmentAsync(candles, "kraken-ohlcvt-csv", sourceVersion, s_importTime.AddHours(1));

        Assert.Equal(HistoricalDatasetWriteResult.Inserted, first.Outcome);
        Assert.Equal(HistoricalDatasetWriteResult.Duplicate, repeat.Outcome);
        Assert.Equal(first.Dataset.VersionIdentity, repeat.Dataset.VersionIdentity);
        Assert.Equal(first.Dataset.CreatedAtUtc, repeat.Dataset.CreatedAtUtc);
        Assert.Equal(1, payloads.StoreCount);
        Assert.Single(context.HistoricalDatasets);
        Assert.Equal(HistoricalCandleFingerprint.Compute(candles),
            HistoricalCandleFingerprint.Compute(await service.ReadAsync(first.Dataset.VersionIdentity)));
        Assert.Equal("kraken-ohlcvt-csv", first.Dataset.Source);
        Assert.Equal(sourceVersion, first.Dataset.SourceVersion);
    }

    [Fact]
    public async Task RefusesMissingAndTamperedBlobsRatherThanTreatingARegisteredManifestAsHealthy()
    {
        await using var context = CreateContext();
        var manifests = new EfHistoricalDatasetRepository(context);
        var payloads = new FakePayloadStore(manifests);
        var service = new HistoricalArchiveIngestionService(payloads, manifests);
        var candles = Candles(10m, 11m, 12m);
        var fingerprint = new string('B', 64);
        var imported = await service.ImportSegmentAsync(candles, "kraken-ohlcvt-csv", fingerprint, s_importTime);

        payloads.Tamper(imported.Dataset.VersionIdentity, Candles(10m, 99m, 12m));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReadAsync(imported.Dataset.VersionIdentity));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ImportSegmentAsync(candles, "kraken-ohlcvt-csv", fingerprint, s_importTime));

        payloads.Remove(imported.Dataset.VersionIdentity);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ReadAsync(imported.Dataset.VersionIdentity));
    }

    [Fact]
    public async Task GapRunsHaveDistinctManifestsAndNeverCrossAnUnobservedInterval()
    {
        var csv = string.Join('\n',
            $"{s_start.ToUnixTimeSeconds()},10,11,9,10,1,1",
            $"{s_start.AddMinutes(1).ToUnixTimeSeconds()},10,11,9,10,1,1",
            $"{s_start.AddMinutes(4).ToUnixTimeSeconds()},10,11,9,10,1,1");
        using var reader = new StringReader(csv);
        var archive = await KrakenOhlcvtArchiveReader.ReadAsync(
            reader, "BTC/EUR", CandleInterval.OneMinute, s_importTime);
        await using var context = CreateContext();
        var service = new HistoricalArchiveIngestionService(
            new FakePayloadStore(new EfHistoricalDatasetRepository(context)),
            new EfHistoricalDatasetRepository(context));

        var imported = new List<HistoricalArchiveImportResult>();
        foreach (var segment in archive.Segments)
            imported.Add(await service.ImportSegmentAsync(
                segment, "kraken-ohlcvt-csv", archive.NormalizedCsvFingerprint, s_importTime));

        Assert.Equal(2, imported.Count);
        Assert.NotEqual(imported[0].Dataset.VersionIdentity, imported[1].Dataset.VersionIdentity);
        Assert.Equal([2, 1], imported.Select(result => result.Dataset.CandleCount));
        Assert.Single(archive.Gaps);
        Assert.Equal(2, context.HistoricalDatasets.Count());
    }

    [Fact]
    public async Task UnsafeOrNoncontiguousEvidenceNeverPublishesAManifest()
    {
        await using var context = CreateContext();
        var manifests = new EfHistoricalDatasetRepository(context);
        var payloads = new FakePayloadStore(manifests);
        var service = new HistoricalArchiveIngestionService(payloads, manifests);
        var invalid = Candles(10m, 11m, 12m);
        invalid[1] = new Candle("BTC/EUR", CandleInterval.OneMinute,
            s_start.AddMinutes(2), s_start.AddMinutes(3),
            11m, 11m, 11m, 11m, 1m, true, false);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ImportSegmentAsync(invalid, "kraken-ohlcvt-csv", new string('C', 64), s_importTime));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ImportSegmentAsync(Candles(10m), "kraken-ohlcvt-csv", "not-a-hash", s_importTime));
        Assert.Empty(context.HistoricalDatasets);
        Assert.Equal(0, payloads.StoreCount);
    }

    [Fact]
    public void BlobJsonRoundTripPreservesTheExactCandleFingerprint()
    {
        var source = Candles(10.01m, 11.7m, 9.995m);
        var copied = JsonSerializer.Deserialize<Candle[]>(JsonSerializer.SerializeToUtf8Bytes(source));
        Assert.NotNull(copied);
        Assert.Equal(HistoricalCandleFingerprint.Compute(source),
            HistoricalCandleFingerprint.Compute(copied));
    }

    private static List<Candle> Candles(params decimal[] closes) =>
        closes.Select((close, index) =>
            new Candle("BTC/EUR", CandleInterval.OneMinute,
                s_start.AddMinutes(index), s_start.AddMinutes(index + 1),
                close, close, close, close, 1m, true, false)).ToList();

    private static TradingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class FakePayloadStore(IHistoricalDatasetRepository manifests) : IHistoricalCandlePayloadStore
    {
        private readonly Dictionary<string, IReadOnlyList<Candle>> _contents = new(StringComparer.Ordinal);
        public int StoreCount { get; private set; }

        public async Task<HistoricalDatasetWriteResult> StoreAsync(
            HistoricalDataset dataset, IReadOnlyList<Candle> candles, CancellationToken cancellationToken = default)
        {
            Assert.Null(await manifests.GetAsync(dataset.VersionIdentity, cancellationToken));
            HistoricalCandleEvidenceValidator.Validate(dataset, candles);
            StoreCount++;
            _contents.Add(dataset.VersionIdentity, Array.AsReadOnly(candles.ToArray()));
            return HistoricalDatasetWriteResult.Inserted;
        }

        public Task<IReadOnlyList<Candle>> ReadAsync(
            HistoricalDataset dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candles = _contents.TryGetValue(dataset.VersionIdentity, out var existing)
                ? existing : throw new FileNotFoundException("Missing historical blob.");
            HistoricalCandleEvidenceValidator.Validate(dataset, candles);
            return Task.FromResult(candles);
        }

        public void Tamper(string version, IReadOnlyList<Candle> candles) => _contents[version] = candles;
        public void Remove(string version) => _contents.Remove(version);
    }
}
