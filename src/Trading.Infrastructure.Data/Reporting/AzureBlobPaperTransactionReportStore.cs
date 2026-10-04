using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Trading.Application.Reporting;

namespace Trading.Infrastructure.Data.Reporting;

public sealed class AzureBlobPaperTransactionReportStore(BlobContainerClient container)
    : IPaperTransactionReportStore
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private readonly BlobContainerClient _container = container
        ?? throw new ArgumentNullException(nameof(container));

    public async Task StoreAsync(Guid ownerId, Guid reportId, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var blob = _container.GetBlobClient(Name(ownerId, reportId));
        if (content.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("Paper report export exceeds the size limit.");
        await blob.UploadAsync(new BinaryData(content),
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadAsync(Guid ownerId, Guid reportId,
        CancellationToken cancellationToken = default)
    {
        var response = await _container.GetBlobClient(Name(ownerId, reportId))
            .DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var stream = response.Value.Content;
        await using var streamHandle = stream.ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (content.Length > MaximumBytes - read)
                throw new InvalidDataException("Stored paper report exceeds the size limit.");
            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return content.ToArray();
    }

    private static string Name(Guid ownerId, Guid reportId)
    {
        if (ownerId == Guid.Empty || reportId == Guid.Empty)
            throw new ArgumentException("A report export requires a valid owner and identifier.");
        return $"paper-reports/v1/{ownerId:N}/{reportId:N}.json";
    }
}
