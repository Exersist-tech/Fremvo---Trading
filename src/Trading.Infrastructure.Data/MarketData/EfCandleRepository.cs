using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Infrastructure.Data.MarketData;

public sealed class EfCandleRepository : ICandleRepository
{
    private readonly TradingDbContext _dbContext;

    public EfCandleRepository(TradingDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<CandleWriteResult> UpsertAsync(Candle candle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candle);
        cancellationToken.ThrowIfCancellationRequested();

        var openTimeUtc = candle.OpenTimeUtc.ToUniversalTime();
        var existing = await FindAsync(candle.Symbol, candle.Interval, openTimeUtc, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return IsEquivalent(existing, candle) ? CandleWriteResult.Duplicate : CandleWriteResult.Conflict;
        }

        var persisted = ToPersisted(candle);
        _dbContext.Candles.Add(persisted);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return CandleWriteResult.Inserted;
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(persisted).State = EntityState.Detached;
            existing = await FindAsync(candle.Symbol, candle.Interval, openTimeUtc, cancellationToken).ConfigureAwait(false);

            if (existing is not null)
            {
                return IsEquivalent(existing, candle) ? CandleWriteResult.Duplicate : CandleWriteResult.Conflict;
            }

            throw;
        }
    }

    public async Task<Candle?> GetLatestAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(symbol, interval);

        var persisted = await _dbContext.Candles
            .AsNoTracking()
            .Where(candle => candle.Symbol == symbol && candle.Interval == interval)
            .OrderByDescending(candle => candle.CloseTimeUtc)
            .ThenByDescending(candle => candle.OpenTimeUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return persisted is null ? null : ToDomain(persisted);
    }

    public async Task<IReadOnlyCollection<Candle>> ListAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(symbol, interval);

        var from = fromUtc.ToUniversalTime();
        var to = toUtc.ToUniversalTime();
        if (from > to)
        {
            throw new ArgumentException("The start time must not be after the end time.", nameof(fromUtc));
        }

        var persisted = await _dbContext.Candles
            .AsNoTracking()
            .Where(candle =>
                candle.Symbol == symbol &&
                candle.Interval == interval &&
                candle.OpenTimeUtc >= from &&
                candle.OpenTimeUtc <= to)
            .OrderBy(candle => candle.OpenTimeUtc)
            .ThenBy(candle => candle.CloseTimeUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return persisted.Select(ToDomain).ToArray();
    }

    private async Task<PersistedCandle?> FindAsync(
        string symbol,
        CandleInterval interval,
        DateTimeOffset openTimeUtc,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Candles
            .SingleOrDefaultAsync(
                stored => stored.Symbol == symbol &&
                    stored.Interval == interval &&
                    stored.OpenTimeUtc == openTimeUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateKey(string symbol, CandleInterval interval)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (interval == CandleInterval.None)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval is required.");
        }
    }

    private static PersistedCandle ToPersisted(Candle candle) => new()
    {
        Symbol = candle.Symbol,
        Interval = candle.Interval,
        OpenTimeUtc = candle.OpenTimeUtc.ToUniversalTime(),
        CloseTimeUtc = candle.CloseTimeUtc.ToUniversalTime(),
        Open = candle.Open,
        High = candle.High,
        Low = candle.Low,
        Close = candle.Close,
        Volume = candle.Volume,
        IsClosed = candle.IsClosed,
        IsDerived = candle.IsDerived,
        QualityFlags = SerializeQualityFlags(candle.QualityFlags)
    };

    private static Candle ToDomain(PersistedCandle candle) => new(
        candle.Symbol,
        candle.Interval,
        candle.OpenTimeUtc.ToUniversalTime(),
        candle.CloseTimeUtc.ToUniversalTime(),
        candle.Open,
        candle.High,
        candle.Low,
        candle.Close,
        candle.Volume,
        candle.IsClosed,
        candle.IsDerived,
        DeserializeQualityFlags(candle.QualityFlags));

    private static bool IsEquivalent(PersistedCandle persisted, Candle candle) =>
        persisted.CloseTimeUtc == candle.CloseTimeUtc.ToUniversalTime() &&
        persisted.Open == candle.Open &&
        persisted.High == candle.High &&
        persisted.Low == candle.Low &&
        persisted.Close == candle.Close &&
        persisted.Volume == candle.Volume &&
        persisted.IsClosed == candle.IsClosed &&
        persisted.IsDerived == candle.IsDerived &&
        string.Equals(persisted.QualityFlags, SerializeQualityFlags(candle.QualityFlags), StringComparison.Ordinal);

    private static string SerializeQualityFlags(IReadOnlyCollection<DataQualityIssue> qualityFlags) =>
        string.Join(
            ',',
            qualityFlags
                .Distinct()
                .OrderBy(flag => (int)flag)
                .Select(flag => ((int)flag).ToString(CultureInfo.InvariantCulture)));

    private static DataQualityIssue[] DeserializeQualityFlags(string qualityFlags)
    {
        if (string.IsNullOrEmpty(qualityFlags))
        {
            return Array.Empty<DataQualityIssue>();
        }

        return qualityFlags
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => (DataQualityIssue)int.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
    }
}
