using Microsoft.EntityFrameworkCore;
using Trading.Backtesting;

namespace Trading.Infrastructure.Data.Backtesting;

public sealed class EfHistoricalDatasetRepository : IHistoricalDatasetRepository
{
    private readonly TradingDbContext _context;

    public EfHistoricalDatasetRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<HistoricalDatasetWriteResult> StoreAsync(
        HistoricalDataset dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        cancellationToken.ThrowIfCancellationRequested();

        var existing = await FindAsync(dataset.VersionIdentity, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return IsEquivalent(existing, dataset)
                ? HistoricalDatasetWriteResult.Duplicate
                : HistoricalDatasetWriteResult.Conflict;
        }

        var persisted = ToPersisted(dataset);
        _context.HistoricalDatasets.Add(persisted);
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return HistoricalDatasetWriteResult.Inserted;
        }
        catch (DbUpdateException)
        {
            _context.Entry(persisted).State = EntityState.Detached;
            existing = await FindAsync(dataset.VersionIdentity, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return IsEquivalent(existing, dataset)
                    ? HistoricalDatasetWriteResult.Duplicate
                    : HistoricalDatasetWriteResult.Conflict;
            }

            throw;
        }
    }

    public async Task<HistoricalDataset?> GetAsync(
        string versionIdentity,
        CancellationToken cancellationToken = default)
    {
        ValidateVersionIdentity(versionIdentity);
        var persisted = await _context.HistoricalDatasets
            .AsNoTracking()
            .SingleOrDefaultAsync(dataset => dataset.VersionIdentity == versionIdentity, cancellationToken)
            .ConfigureAwait(false);
        return persisted is null ? null : ToDomain(persisted);
    }

    public async Task<IReadOnlyList<HistoricalDataset>> ListAsync(
        string symbol,
        string interval,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (string.IsNullOrWhiteSpace(interval))
        {
            throw new ArgumentException("Interval is required.", nameof(interval));
        }

        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        }

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        var normalizedInterval = HistoricalDataset.CanonicalizeInterval(interval);
        if (to < from)
        {
            throw new ArgumentException("End time must not precede start time.", nameof(toUtc));
        }

        var results = await _context.HistoricalDatasets
            .AsNoTracking()
            .Where(dataset =>
                dataset.Symbol == symbol.Trim() &&
                dataset.Interval == normalizedInterval &&
                dataset.FromUtc >= from &&
                dataset.ToUtc <= to)
            .OrderBy(dataset => dataset.FromUtc)
            .ThenBy(dataset => dataset.ToUtc)
            .ThenBy(dataset => dataset.CreatedAtUtc)
            .ThenBy(dataset => dataset.VersionIdentity)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return results.Select(ToDomain).ToArray();
    }

    internal static PersistedHistoricalDataset ToPersisted(HistoricalDataset dataset) => new()
    {
        VersionIdentity = dataset.VersionIdentity,
        Id = dataset.Id,
        Source = dataset.Source,
        Symbol = dataset.Symbol,
        Interval = dataset.Interval,
        FromUtc = dataset.FromUtc.ToUniversalTime(),
        ToUtc = dataset.ToUtc.ToUniversalTime(),
        CandleCount = dataset.CandleCount,
        ContentFingerprint = dataset.ContentFingerprint,
        SourceVersion = dataset.SourceVersion,
        CreatedAtUtc = dataset.CreatedAtUtc.ToUniversalTime(),
        ContainsOnlyClosedCandles = dataset.ContainsOnlyClosedCandles
    };

    internal static HistoricalDataset ToDomain(PersistedHistoricalDataset dataset)
    {
        if (!dataset.ContainsOnlyClosedCandles)
        {
            throw new InvalidOperationException("Persisted historical datasets must contain only closed candles.");
        }

        return new HistoricalDataset(
            dataset.Id,
            dataset.Source,
            dataset.Symbol,
            dataset.Interval,
            dataset.FromUtc.ToUniversalTime(),
            dataset.ToUtc.ToUniversalTime(),
            dataset.CandleCount,
            dataset.ContentFingerprint,
            dataset.SourceVersion,
            dataset.CreatedAtUtc.ToUniversalTime());
    }

    private Task<PersistedHistoricalDataset?> FindAsync(string versionIdentity, CancellationToken cancellationToken) =>
        _context.HistoricalDatasets.SingleOrDefaultAsync(
            dataset => dataset.VersionIdentity == versionIdentity,
            cancellationToken);

    private static bool IsEquivalent(PersistedHistoricalDataset existing, HistoricalDataset dataset) =>
        existing.ContainsOnlyClosedCandles &&
        existing.Id == dataset.Id &&
        existing.Source == dataset.Source &&
        existing.Symbol == dataset.Symbol &&
        existing.Interval == dataset.Interval &&
        existing.FromUtc == dataset.FromUtc.ToUniversalTime() &&
        existing.ToUtc == dataset.ToUtc.ToUniversalTime() &&
        existing.CandleCount == dataset.CandleCount &&
        existing.ContentFingerprint == dataset.ContentFingerprint &&
        existing.SourceVersion == dataset.SourceVersion &&
        existing.CreatedAtUtc == dataset.CreatedAtUtc.ToUniversalTime();

    private static void ValidateVersionIdentity(string versionIdentity)
    {
        if (string.IsNullOrWhiteSpace(versionIdentity) ||
            versionIdentity.Trim().Length != 64 ||
            !versionIdentity.Trim().All(character =>
                (character >= 'A' && character <= 'F') || (character >= '0' && character <= '9')))
        {
            throw new ArgumentException("Version identity must be an uppercase SHA-256 hexadecimal digest.", nameof(versionIdentity));
        }
    }
}
