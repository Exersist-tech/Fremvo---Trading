using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Scanner;
using Trading.Domain.Market;
using Trading.Domain.Scanner;

namespace Trading.Infrastructure.Data.Scanner;

/// <summary>
/// Entity Framework storage for owner-isolated scan definitions.
/// </summary>
public sealed class EfScanRequestRepository : IScanRequestRepository
{
    private readonly TradingDbContext _context;

    public EfScanRequestRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task AddAsync(Guid ownerId, ScanRequest request, CancellationToken cancellationToken = default)
    {
        ValidateOwner(ownerId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.OwnerId != ownerId)
        {
            throw new InvalidOperationException("A scan request can only be written by its owner.");
        }

        _context.ScanRequests.Add(ToPersisted(request));
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanRequest?> GetAsync(
        Guid ownerId,
        Guid scanRequestId,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnerAndRequest(ownerId, scanRequestId);

        var persisted = await _context.ScanRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                request => request.Id == scanRequestId && request.OwnerId == ownerId,
                cancellationToken)
            .ConfigureAwait(false);

        return persisted is null ? null : ToDomain(persisted);
    }

    public async Task<IReadOnlyList<ScanRequest>> ListAsync(
        Guid ownerId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateOwner(ownerId);
        ValidateLimit(limit);

        var persisted = await _context.ScanRequests
            .AsNoTracking()
            .Where(request => request.OwnerId == ownerId)
            .OrderByDescending(request => request.CreatedAtUtc)
            .ThenBy(request => request.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return persisted.Select(ToDomain).ToArray();
    }

    internal static PersistedScanRequest ToPersisted(ScanRequest request) => new()
    {
        Id = request.Id,
        OwnerId = request.OwnerId,
        Name = request.Name,
        Symbols = JsonSerializer.Serialize(request.Symbols),
        Interval = request.Interval,
        Criteria = JsonSerializer.Serialize(request.Criteria.Select(criterion => new PersistedCriterion(
            criterion.Kind,
            criterion.Threshold))),
        ResultLimit = request.ResultLimit,
        CreatedAtUtc = request.CreatedAtUtc.ToUniversalTime()
    };

    internal static ScanRequest ToDomain(PersistedScanRequest request) =>
        new(
            request.Id,
            request.OwnerId,
            request.Name,
            Deserialize<string[]>(request.Symbols, "symbols"),
            request.Interval,
            Deserialize<PersistedCriterion[]>(request.Criteria, "criteria")
                .Select(criterion => new ScanCriterion(criterion.Kind, criterion.Threshold)),
            request.ResultLimit,
            request.CreatedAtUtc.ToUniversalTime());

    internal static T Deserialize<T>(string json, string fieldName) =>
        JsonSerializer.Deserialize<T>(json) ??
        throw new InvalidOperationException($"Persisted scan {fieldName} cannot be null.");

    internal static void ValidateOwner(Guid ownerId)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("Owner id is required.", nameof(ownerId));
        }
    }

    internal static void ValidateOwnerAndRequest(Guid ownerId, Guid scanRequestId)
    {
        ValidateOwner(ownerId);
        if (scanRequestId == Guid.Empty)
        {
            throw new ArgumentException("Scan request id is required.", nameof(scanRequestId));
        }
    }

    internal static void ValidateLimit(int limit)
    {
        if (limit < 1 || limit > ScanRequest.MaximumResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"The result limit must be between 1 and {ScanRequest.MaximumResults}.");
        }
    }

    internal sealed record PersistedCriterion(ScanCriterionKind Kind, decimal Threshold);
}

/// <summary>
/// Entity Framework storage for informational scan evidence.
/// </summary>
public sealed class EfScanResultRepository : IScanResultRepository
{
    private readonly TradingDbContext _context;

    public EfScanResultRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<ScanResultWriteResult> RecordAsync(
        Guid ownerId,
        ScanResult result,
        CancellationToken cancellationToken = default)
    {
        EfScanRequestRepository.ValidateOwner(ownerId);
        ArgumentNullException.ThrowIfNull(result);
        if (result.OwnerId != ownerId)
        {
            throw new InvalidOperationException("A scan result can only be written by its request owner.");
        }

        var persistedRequest = await _context.ScanRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                request => request.Id == result.ScanRequestId && request.OwnerId == ownerId,
                cancellationToken)
            .ConfigureAwait(false);
        if (persistedRequest is null)
        {
            throw new InvalidOperationException("The owner-scoped scan request does not exist.");
        }

        ValidateResultAgainstRequest(result, EfScanRequestRepository.ToDomain(persistedRequest));

        var existing = await FindAsync(result, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return IsEquivalent(existing, result) ? ScanResultWriteResult.Duplicate : ScanResultWriteResult.Conflict;
        }

        var persisted = ToPersisted(result);
        _context.ScanResults.Add(persisted);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ScanResultWriteResult.Inserted;
        }
        catch (DbUpdateException)
        {
            _context.Entry(persisted).State = EntityState.Detached;
            existing = await FindAsync(result, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return IsEquivalent(existing, result) ? ScanResultWriteResult.Duplicate : ScanResultWriteResult.Conflict;
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<ScanResult>> ListAsync(
        Guid ownerId,
        Guid scanRequestId,
        Guid scanRunId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        EfScanRequestRepository.ValidateOwnerAndRequest(ownerId, scanRequestId);
        if (scanRunId == Guid.Empty)
        {
            throw new ArgumentException("Scan run id is required.", nameof(scanRunId));
        }

        EfScanRequestRepository.ValidateLimit(limit);
        var persisted = await _context.ScanResults
            .AsNoTracking()
            .Where(result =>
                result.OwnerId == ownerId &&
                result.ScanRequestId == scanRequestId &&
                result.ScanRunId == scanRunId)
            .OrderBy(result => result.Rank)
            .ThenByDescending(result => result.Score)
            .ThenBy(result => result.Symbol)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return persisted.Select(ToDomain).ToArray();
    }

    private Task<PersistedScanResult?> FindAsync(ScanResult result, CancellationToken cancellationToken) =>
        _context.ScanResults.SingleOrDefaultAsync(
            candidate =>
                candidate.ScanRequestId == result.ScanRequestId &&
                candidate.ScanRunId == result.ScanRunId &&
                candidate.Symbol == result.Symbol,
            cancellationToken);

    private static PersistedScanResult ToPersisted(ScanResult result) => new()
    {
        OwnerId = result.OwnerId,
        ScanRequestId = result.ScanRequestId,
        ScanRunId = result.ScanRunId,
        Symbol = result.Symbol,
        Rank = result.Rank,
        Score = result.Score,
        MatchedCriteria = JsonSerializer.Serialize(result.MatchedCriteria),
        EvidenceAsOfUtc = result.EvidenceAsOfUtc.ToUniversalTime(),
        EvaluatedAtUtc = result.EvaluatedAtUtc.ToUniversalTime()
    };

    private static ScanResult ToDomain(PersistedScanResult result) =>
        new(
            result.OwnerId,
            result.ScanRequestId,
            result.ScanRunId,
            result.Symbol,
            result.Rank,
            result.Score,
            EfScanRequestRepository.Deserialize<ScanCriterionKind[]>(result.MatchedCriteria, "matched criteria"),
            result.EvidenceAsOfUtc.ToUniversalTime(),
            result.EvaluatedAtUtc.ToUniversalTime());

    private static void ValidateResultAgainstRequest(ScanResult result, ScanRequest request)
    {
        if (!request.Symbols.Contains(result.Symbol, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("A scan result symbol must be in the request's symbol scope.");
        }

        if (result.Rank > request.ResultLimit)
        {
            throw new InvalidOperationException("A scan result rank cannot exceed the request's result limit.");
        }

        if (result.MatchedCriteria.Any(kind => request.Criteria.All(criterion => criterion.Kind != kind)))
        {
            throw new InvalidOperationException("A scan result may only report criteria configured on its request.");
        }
    }

    private static bool IsEquivalent(PersistedScanResult existing, ScanResult result) =>
        existing.OwnerId == result.OwnerId &&
        existing.Rank == result.Rank &&
        existing.Score == result.Score &&
        existing.EvidenceAsOfUtc == result.EvidenceAsOfUtc.ToUniversalTime() &&
        existing.EvaluatedAtUtc == result.EvaluatedAtUtc.ToUniversalTime() &&
        string.Equals(
            existing.MatchedCriteria,
            JsonSerializer.Serialize(result.MatchedCriteria),
            StringComparison.Ordinal);
}
