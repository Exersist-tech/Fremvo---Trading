using System.Net;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Trading.Infrastructure.Data.Reporting;

namespace Trading.ArchitectureTests;

public sealed class AzureBlobPaperTransactionReportStoreTests
{
    [Fact]
    public async Task CreatesPrivateOwnerPathWithoutReplacingOrAcceptingOversizedData()
    {
        using var handler = new ReportBlobHandler();
        using var client = new HttpClient(handler);
        var store = new AzureBlobPaperTransactionReportStore(new BlobContainerClient(
            new Uri("https://storage.test/paper-reports"),
            new BlobClientOptions { Transport = new HttpClientTransport(client) }));
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var content = "{\"report\":\"paper only\"}"u8.ToArray();

        await store.StoreAsync(owner, id, content);
        Assert.Equal(content, await store.ReadAsync(owner, id));
        Assert.Equal("*", handler.IfNoneMatch);
        Assert.Equal($"paper-reports/paper-reports/v1/{owner:N}/{id:N}.json", handler.Path);
        await Assert.ThrowsAsync<RequestFailedException>(() => store.StoreAsync(owner, id, content));
        var missing = await Assert.ThrowsAsync<RequestFailedException>(() =>
            store.ReadAsync(Guid.NewGuid(), id));
        Assert.Equal(404, missing.Status);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.StoreAsync(owner, Guid.NewGuid(), new byte[AzureBlobPaperTransactionReportStore.MaximumBytes + 1]));
        handler.Replace(content: new byte[AzureBlobPaperTransactionReportStore.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(owner, id));
    }

    private sealed class ReportBlobHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        private byte[]? _replacement;
        public string? Path { get; private set; }
        public string? IfNoneMatch { get; private set; }

        public void Replace(byte[] content) => _replacement = content;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath.Trim('/');
            if (request.Method == HttpMethod.Put)
            {
                IfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.Tag;
                if (_blobs.ContainsKey(Path))
                    return Reply(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                _blobs.Add(Path, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                return Reply(HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get)
            {
                if (!_blobs.TryGetValue(Path, out var stored))
                    return Reply(HttpStatusCode.NotFound, "BlobNotFound");
                var response = Reply(HttpStatusCode.OK);
                response.Content = new ByteArrayContent(_replacement ?? stored);
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                return response;
            }
            throw new InvalidOperationException("Unexpected Blob request.");
        }

        private static HttpResponseMessage Reply(HttpStatusCode code, string? errorCode = null)
        {
            var response = new HttpResponseMessage(code);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"report-etag\"");
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
