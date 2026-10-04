using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Application.Backtesting;

/// <summary>
/// Imports one independently replayable contiguous run. The payload is written before
/// its discoverable SQL manifest; a failed manifest write can leave only an orphan blob.
/// Source authentication and the completeness of a multi-run archive are separate gates.
/// </summary>
public sealed class HistoricalArchiveIngestionService
{
    private readonly IHistoricalCandlePayloadStore _payloads;
    private readonly IHistoricalDatasetRepository _manifests;

    public HistoricalArchiveIngestionService(
        IHistoricalCandlePayloadStore payloads,
        IHistoricalDatasetRepository manifests)
    {
        _payloads = payloads ?? throw new ArgumentNullException(nameof(payloads));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
    }

    public async Task<HistoricalArchiveImportResult> ImportSegmentAsync(
        IReadOnlyList<Candle> candles,
        string source,
        string sourceVersion,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersion);
        if (candles.Count is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(candles), "Import one contiguous run of at most 100,000 candles.");
        if (importedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Archive import time must be UTC.", nameof(importedAtUtc));
        if (sourceVersion is not { Length: 64 }
            || sourceVersion.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Source version must be a SHA-256 digest of the source archive.", nameof(sourceVersion));
        cancellationToken.ThrowIfCancellationRequested();
        sourceVersion = sourceVersion.ToUpperInvariant();

        var first = candles[0] ?? throw new ArgumentException("A candle cannot be null.", nameof(candles));
        var last = candles[^1] ?? throw new ArgumentException("A candle cannot be null.", nameof(candles));
        var contentFingerprint = HistoricalCandleFingerprint.Compute(candles);
        var dataset = new HistoricalDataset(
            $"archive-{contentFingerprint}", source, first.Symbol, IntervalCode(first.Interval),
            first.OpenTimeUtc, last.OpenTimeUtc, candles.Count, contentFingerprint,
            sourceVersion, importedAtUtc);
        HistoricalCandleEvidenceValidator.Validate(dataset, candles);

        var existing = await _manifests.GetAsync(dataset.VersionIdentity, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await VerifyExistingAsync(existing, candles, cancellationToken).ConfigureAwait(false);

        var blobOutcome = await _payloads.StoreAsync(dataset, candles, cancellationToken).ConfigureAwait(false);
        if (blobOutcome == HistoricalDatasetWriteResult.Conflict)
            throw new InvalidOperationException("Historical candle blob conflicts with its immutable manifest.");
        if (blobOutcome == HistoricalDatasetWriteResult.Duplicate)
            await _payloads.ReadAsync(dataset, cancellationToken).ConfigureAwait(false);

        var outcome = await _manifests.StoreAsync(dataset, cancellationToken).ConfigureAwait(false);
        if (outcome == HistoricalDatasetWriteResult.Inserted)
            return new(dataset, outcome);

        existing = await _manifests.GetAsync(dataset.VersionIdentity, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A conflicting historical dataset disappeared during import.");
        return await VerifyExistingAsync(existing, candles, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Candle>> ReadAsync(
        string versionIdentity,
        CancellationToken cancellationToken = default)
    {
        var dataset = await _manifests.GetAsync(versionIdentity, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The historical dataset manifest is not registered.");
        var candles = await _payloads.ReadAsync(dataset, cancellationToken).ConfigureAwait(false);
        HistoricalCandleEvidenceValidator.Validate(dataset, candles);
        return candles;
    }

    private async Task<HistoricalArchiveImportResult> VerifyExistingAsync(
        HistoricalDataset existing,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken)
    {
        HistoricalCandleEvidenceValidator.Validate(existing, candles);
        await _payloads.ReadAsync(existing, cancellationToken).ConfigureAwait(false);
        return new(existing, HistoricalDatasetWriteResult.Duplicate);
    }

    private static string IntervalCode(CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute => "1M",
        CandleInterval.FiveMinutes => "5M",
        CandleInterval.TenMinutes => "10M",
        CandleInterval.FifteenMinutes => "15M",
        CandleInterval.ThirtyMinutes => "30M",
        CandleInterval.OneHour => "1H",
        CandleInterval.FourHours => "4H",
        CandleInterval.OneDay => "1D",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), "Interval is not approved for historical replay.")
    };
}

public sealed record HistoricalArchiveImportResult(
    HistoricalDataset Dataset,
    HistoricalDatasetWriteResult Outcome);
