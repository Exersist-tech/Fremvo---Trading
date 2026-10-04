using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.Infrastructure.Data.Backtesting;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class AzureBlobHistoricalCandlePayloadStoreTests
{
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WritesCreateOnlyReadsBackAndChecksExistingPayloadBeforeDuplicate()
    {
        using var handler = new BlobHandler();
        using var client = new HttpClient(handler);
        var options = new BlobClientOptions { Transport = new HttpClientTransport(client) };
        var blobs = new AzureBlobHistoricalCandlePayloadStore(new BlobContainerClient(
            new Uri("https://storage.test/research-history"), options));
        var candles = Candles(10m, 11m);
        var manifest = Dataset(candles);

        Assert.Equal(HistoricalDatasetWriteResult.Inserted,
            await blobs.StoreAsync(manifest, candles));
        Assert.Equal(HistoricalDatasetWriteResult.Duplicate,
            await blobs.StoreAsync(manifest, candles));
        Assert.Equal(2, handler.UploadCount);
        Assert.Equal(2, handler.DownloadCount);
        Assert.Equal($"research-history/historical-candles/v1/{manifest.VersionIdentity}.json",
            handler.BlobPath);
        Assert.Equal("*", handler.IfNoneMatch);
        Assert.Equal(HistoricalCandleFingerprint.Compute(candles),
            HistoricalCandleFingerprint.Compute(await blobs.ReadAsync(manifest)));

        handler.ReplaceContent(JsonSerializer.SerializeToUtf8Bytes(Candles(10m, 99m)));
        await Assert.ThrowsAsync<ArgumentException>(() => blobs.ReadAsync(manifest));
        await Assert.ThrowsAsync<ArgumentException>(() => blobs.StoreAsync(manifest, candles));
    }

    [Fact]
    public async Task MissingBlobIsNotAHealthyDataset()
    {
        using var handler = new BlobHandler();
        using var client = new HttpClient(handler);
        var blobs = new AzureBlobHistoricalCandlePayloadStore(new BlobContainerClient(
            new Uri("https://storage.test/research-history"),
            new BlobClientOptions { Transport = new HttpClientTransport(client) }));

        var error = await Assert.ThrowsAsync<RequestFailedException>(() =>
            blobs.ReadAsync(Dataset(Candles(10m, 11m))));
        Assert.Equal(404, error.Status);
    }

    private static List<Candle> Candles(params decimal[] prices) =>
        prices.Select((price, index) => new Candle(
            "BTC/EUR", CandleInterval.OneMinute, s_start.AddMinutes(index),
            s_start.AddMinutes(index + 1), price, price, price, price, 1m,
            isClosed: true, isDerived: false)).ToList();

    private static HistoricalDataset Dataset(List<Candle> candles) =>
        new("fixture", "kraken-ohlcvt-csv", "BTC/EUR", "1m",
            candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc, candles.Count,
            HistoricalCandleFingerprint.Compute(candles), new string('A', 64),
            s_start.AddHours(1));

    private sealed class BlobHandler : HttpMessageHandler
    {
        private byte[]? _payload;
        public int UploadCount { get; private set; }
        public int DownloadCount { get; private set; }
        public string? BlobPath { get; private set; }
        public string? IfNoneMatch { get; private set; }

        public void ReplaceContent(byte[] content) => _payload = content;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BlobPath = request.RequestUri!.AbsolutePath.Trim('/');
            if (request.Method == HttpMethod.Put)
            {
                UploadCount++;
                IfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.Tag;
                if (_payload is not null)
                    return Reply(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                _payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return Reply(HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get)
            {
                DownloadCount++;
                if (_payload is null)
                    return Reply(HttpStatusCode.NotFound, "BlobNotFound");
                var response = Reply(HttpStatusCode.OK);
                response.Content = new ByteArrayContent(_payload);
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                return response;
            }

            throw new InvalidOperationException($"Unexpected Blob HTTP method: {request.Method}.");
        }

        private static HttpResponseMessage Reply(HttpStatusCode code, string? errorCode = null)
        {
            var response = new HttpResponseMessage(code);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"fixture-etag\"");
            response.Content = new ByteArrayContent([]);
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "fixture-request");
            response.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");
            response.Headers.TryAddWithoutValidation("Last-Modified", "Thu, 01 Jan 2026 00:00:00 GMT");
            if (errorCode is not null)
                response.Headers.TryAddWithoutValidation("x-ms-error-code", errorCode);
            return response;
        }
    }
}
