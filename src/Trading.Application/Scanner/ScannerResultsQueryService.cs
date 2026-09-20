using Trading.Domain.Scanner;

namespace Trading.Application.Scanner;

/// <summary>
/// Provides a bounded, owner-scoped projection of persisted scanner evidence.
/// This service is read-only and cannot start scans or change their definitions.
/// </summary>
public sealed class ScannerResultsQueryService
{
    public const int PageSize = 100;

    private readonly IScanRequestRepository _requests;
    private readonly IScanResultRepository _results;

    public ScannerResultsQueryService(IScanRequestRepository requests, IScanResultRepository results)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _results = results ?? throw new ArgumentNullException(nameof(results));
    }

    public async Task<ScannerResultsPage?> GetAsync(
        Guid ownerId,
        Guid scanRequestId,
        Guid scanRunId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || scanRequestId == Guid.Empty || scanRunId == Guid.Empty)
        {
            throw new ArgumentException("A valid owner, scan request, and scan run are required.");
        }

        var request = await _requests.GetAsync(ownerId, scanRequestId, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return null;
        }

        var results = await _results
            .ListAsync(ownerId, scanRequestId, scanRunId, PageSize, cancellationToken)
            .ConfigureAwait(false);

        return new ScannerResultsPage(
            request.Name,
            request.Symbols,
            request.Interval.ToString(),
            results.Select(result => ToRow(request, result)).ToArray());
    }

    private static ScannerResultRow ToRow(ScanRequest request, ScanResult result) =>
        new(
            result.Symbol,
            result.Rank,
            result.Score,
            result.EvidenceAsOfUtc,
            request.Criteria.Select(criterion => new ScannerCriterionRow(
                Label(criterion.Kind),
                result.MatchedCriteria.Contains(criterion.Kind)
                    ? "Passed"
                    : "Unavailable",
                result.MatchedCriteria.Contains(criterion.Kind)
                    ? "Recorded as matched in the scan result."
                    : "No outcome for this criterion is recorded in the scan result.")).ToArray(),
            null);

    private static string Label(ScanCriterionKind kind) => kind switch
    {
        ScanCriterionKind.MinimumClosedCandleCount => "Minimum closed-candle count",
        ScanCriterionKind.MinimumCandleVolume => "Minimum candle volume",
        ScanCriterionKind.MaximumCandleRangePercent => "Maximum candle range percent",
        ScanCriterionKind.CloseAboveSimpleMovingAverage => "Close above simple moving average",
        _ => "Unavailable criterion"
    };
}

public sealed record ScannerResultsPage(
    string ScanName,
    IReadOnlyList<string> SymbolScope,
    string Interval,
    IReadOnlyList<ScannerResultRow> Results);

public sealed record ScannerResultRow(
    string Symbol,
    int Rank,
    decimal Score,
    DateTimeOffset EvidenceAsOfUtc,
    IReadOnlyList<ScannerCriterionRow> Criteria,
    string? DataRejectionReason);

public sealed record ScannerCriterionRow(
    string Label,
    string Status,
    string Rationale);
