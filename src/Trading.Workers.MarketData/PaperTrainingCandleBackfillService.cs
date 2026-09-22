using System.Collections.Concurrent;
using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData;

namespace Trading.Workers.MarketData;

public sealed class PaperTrainingCandleBackfillService
{
    internal const int RequiredCandleCount = 47;

    private static readonly Action<ILogger, string, CandleInterval, int, Exception?> s_logCompleted =
        LoggerMessage.Define<string, CandleInterval, int>(
            LogLevel.Information,
            new EventId(1, "PaperTrainingCandleBackfillCompleted"),
            "Paper-training candle backfill completed. Symbol={Symbol} Interval={Interval} Candles={Candles}.");

    private static readonly Action<ILogger, string, CandleInterval, string, Exception?> s_logSkipped =
        LoggerMessage.Define<string, CandleInterval, string>(
            LogLevel.Warning,
            new EventId(2, "PaperTrainingCandleBackfillSkipped"),
            "Paper-training candle backfill was skipped. Symbol={Symbol} Interval={Interval} Reason={Reason}.");

    private readonly IHistoricalCandleSource _source;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PaperTrainingCandleBackfillService> _logger;
    private readonly ConcurrentDictionary<CandleSubscription, DateTimeOffset> _completedThrough = new();

    public PaperTrainingCandleBackfillService(
        IHistoricalCandleSource source,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<PaperTrainingCandleBackfillService> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureAsync(
        IReadOnlyCollection<CandleSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        foreach (var subscription in subscriptions.Where(item =>
                     PaperTrainingAutoSelectionService.ApprovedIntervals.Contains(item.Interval)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interval = TimeSpan.FromMinutes((int)subscription.Interval);
            var completedBoundary = AlignDown(_timeProvider.GetUtcNow(), interval);
            if (_completedThrough.TryGetValue(subscription, out var completedThrough)
                && completedThrough >= completedBoundary)
            {
                continue;
            }

            IReadOnlyList<Candle> fetched;
            try
            {
                fetched = await _source.FetchAsync(
                    subscription.Symbol,
                    subscription.Interval,
                    completedBoundary - TimeSpan.FromTicks(interval.Ticks * (RequiredCandleCount + 2)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MarketDataSourceException)
            {
                s_logSkipped(
                    _logger,
                    subscription.Symbol,
                    subscription.Interval,
                    "Kraken did not provide authoritative history for this configured subscription.",
                    null);
                continue;
            }
            var candles = SelectContiguousClosedHistory(
                fetched,
                subscription,
                completedBoundary);
            if (candles.Count != RequiredCandleCount)
            {
                s_logSkipped(
                    _logger,
                    subscription.Symbol,
                    subscription.Interval,
                    $"Kraken returned {candles.Count} usable contiguous candles; {RequiredCandleCount} are required.",
                    null);
                continue;
            }

            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<EfCandleRepository>();
            var conflict = false;
            foreach (var candle in candles)
            {
                if (await repository.UpsertAuthoritativeHistoryAsync(candle, cancellationToken).ConfigureAwait(false)
                    == CandleWriteResult.Conflict)
                {
                    conflict = true;
                    break;
                }
            }

            if (conflict)
            {
                s_logSkipped(
                    _logger,
                    subscription.Symbol,
                    subscription.Interval,
                    "Stored OHLCV data conflicts with Kraken's authoritative history.",
                    null);
                continue;
            }

            _completedThrough[subscription] = completedBoundary;
            s_logCompleted(_logger, subscription.Symbol, subscription.Interval, candles.Count, null);
        }
    }

    internal static IReadOnlyList<Candle> SelectContiguousClosedHistory(
        IEnumerable<Candle> fetched,
        CandleSubscription subscription,
        DateTimeOffset completedBoundary)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        ArgumentNullException.ThrowIfNull(subscription);
        var duration = TimeSpan.FromMinutes((int)subscription.Interval);
        var candidates = fetched
            .Where(candle =>
                string.Equals(candle.Symbol, subscription.Symbol, StringComparison.OrdinalIgnoreCase)
                && candle.Interval == subscription.Interval
                && candle.CanBeUsedForClosedCandleSignal
                && !candle.IsDerived
                && candle.CloseTimeUtc <= completedBoundary)
            .OrderBy(candle => candle.OpenTimeUtc)
            .TakeLast(RequiredCandleCount)
            .ToArray();
        if (candidates.Length != RequiredCandleCount)
        {
            return [];
        }

        for (var index = 1; index < candidates.Length; index++)
        {
            if (candidates[index].OpenTimeUtc != candidates[index - 1].CloseTimeUtc
                || candidates[index].CloseTimeUtc - candidates[index].OpenTimeUtc != duration)
            {
                return [];
            }
        }

        return candidates;
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % interval.Ticks), TimeSpan.Zero);
    }
}
