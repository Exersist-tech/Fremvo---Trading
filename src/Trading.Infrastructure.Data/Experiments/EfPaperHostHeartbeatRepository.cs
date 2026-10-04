using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Trading.Domain.Audit;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Infrastructure.Data.Experiments;

public sealed class EfPaperHostHeartbeatRepository(TradingDbContext db)
{
    public const string Scanner = "scanner";
    public const string Experiments = "experiments";
    private const string Action = "PaperHost.Heartbeat";
    private const string ForwardCandleAction = "PaperHost.ForwardCandle";
    private static readonly TimeSpan s_maximumCandleAge = TimeSpan.FromMinutes(10);

    public async Task RecordAsync(string host, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateHost(host);
        if (now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Host heartbeat must use UTC.", nameof(now));
        db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(), null, Action, "PaperHost", host, now, null, null, Guid.NewGuid().ToString("D")));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<DateTimeOffset?> LastAsync(string host, CancellationToken cancellationToken = default)
    {
        ValidateHost(host);
        return db.AuditEvents.AsNoTracking()
            .Where(value => value.Action == Action && value.TargetType == "PaperHost" && value.TargetId == host)
            .OrderByDescending(value => value.OccurredAtUtc)
            .Select(value => (DateTimeOffset?)value.OccurredAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> BothFreshAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Host readiness must use UTC.", nameof(now));
        foreach (var host in new[] { Scanner, Experiments })
        {
            var last = await LastAsync(host, cancellationToken).ConfigureAwait(false);
            if (last is null || last > now || now - last > TimeSpan.FromMinutes(2))
                return false;
        }
        return true;
    }

    public async Task RecordForwardCandleAsync(
        Candle candle, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ValidateUtc(now);
        var interval = TimeSpan.FromMinutes((int)candle.Interval);
        if (candle.Interval is not (CandleInterval.OneMinute or CandleInterval.FiveMinutes)
            || !candle.CanBeUsedForClosedCandleSignal
            || candle.IsDerived
            || candle.QualityFlags.Count != 0
            || candle.Open <= 0m || candle.High <= 0m
            || candle.Low <= 0m || candle.Close <= 0m
            || candle.CloseTimeUtc.Offset != TimeSpan.Zero
            || candle.OpenTimeUtc.Ticks % interval.Ticks != 0
            || candle.CloseTimeUtc - candle.OpenTimeUtc != interval
            || candle.CloseTimeUtc > now
            || now - candle.CloseTimeUtc > s_maximumCandleAge)
            throw new ArgumentException("Forward evidence requires a recent, safe, native closed 1m or 5m candle.", nameof(candle));

        db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(), null, ForwardCandleAction, "PaperHost",
            $"{candle.Symbol}:{candle.Interval}", now, null,
            candle.CloseTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("D")));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasRecentForwardCandleAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ValidateUtc(now);
        var evidence = await db.AuditEvents.AsNoTracking()
            .Where(value => value.Action == ForwardCandleAction && value.TargetType == "PaperHost"
                && value.OccurredAtUtc <= now
                && value.OccurredAtUtc >= now - s_maximumCandleAge)
            .OrderByDescending(value => value.OccurredAtUtc)
            .Select(value => value.After)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.TryParseExact(evidence, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var close)
            && close.Offset == TimeSpan.Zero
            && close <= now
            && now - close <= s_maximumCandleAge;
    }

    private static void ValidateUtc(DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero || now < DateTimeOffset.MinValue.Add(s_maximumCandleAge))
            throw new ArgumentException("Forward market-data evidence requires a valid UTC time.", nameof(now));
    }

    private static void ValidateHost(string host)
    {
        if (host is not (Scanner or Experiments))
            throw new ArgumentException("Unknown paper host identity.", nameof(host));
    }
}
