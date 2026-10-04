using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Trading.Backtesting;
using Trading.MarketData;

namespace Trading.Infrastructure.Data.Backtesting;

/// <summary>
/// Private, create-only blobs. A matching name with different or corrupt content is an error,
/// never a replaceable version of historical research evidence.
/// </summary>
public sealed class AzureBlobHistoricalCandlePayloadStore : IHistoricalCandlePayloadStore
{
    private const int MaximumPayloadBytes = 64 * 1024 * 1024;
    private readonly BlobContainerClient _container;

    public AzureBlobHistoricalCandlePayloadStore(BlobContainerClient container) =>
        _container = container ?? throw new ArgumentNullException(nameof(container));

    public async Task<HistoricalDatasetWriteResult> StoreAsync(
        HistoricalDataset dataset,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(candles);
        cancellationToken.ThrowIfCancellationRequested();
        if (candles.Count is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(candles), "A historical blob must contain at most 100,000 candles.");
        HistoricalCandleEvidenceValidator.Validate(dataset, candles);
        var blob = _container.GetBlobClient(BlobName(dataset));
        var payload = JsonSerializer.SerializeToUtf8Bytes(candles);
        if (payload.Length > MaximumPayloadBytes)
            throw new InvalidDataException("Historical candle blob exceeds the payload size limit.");
        try
        {
            await blob.UploadAsync(
                new BinaryData(payload),
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                },
                cancellationToken).ConfigureAwait(false);
            await ReadAsync(dataset, cancellationToken).ConfigureAwait(false);
            return HistoricalDatasetWriteResult.Inserted;
        }
        catch (RequestFailedException exception)
            when (exception.Status is 409 or 412)
        {
            await ReadAsync(dataset, cancellationToken).ConfigureAwait(false);
            return HistoricalDatasetWriteResult.Duplicate;
        }
    }

    public async Task<IReadOnlyList<Candle>> ReadAsync(
        HistoricalDataset dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var blob = _container.GetBlobClient(BlobName(dataset));
        var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var stream = response.Value.Content;
        await using var streamHandle = stream.ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (content.Length > MaximumPayloadBytes - bytesRead)
                throw new InvalidDataException("Historical candle blob exceeds the payload size limit.");
            await content.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }

        Candle[] candles;
        try
        {
            candles = JsonSerializer.Deserialize<Candle[]>(content.ToArray())
                ?? throw new InvalidDataException("Historical candle blob has no candle payload.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Historical candle blob is not valid candle evidence.", exception);
        }

        HistoricalCandleEvidenceValidator.Validate(dataset, candles);
        return Array.AsReadOnly(candles);
    }

    private static string BlobName(HistoricalDataset dataset) =>
        $"historical-candles/v1/{dataset.VersionIdentity}.json";
}
