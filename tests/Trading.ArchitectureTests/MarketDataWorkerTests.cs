using Microsoft.Extensions.Logging.Abstractions;
using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Workers.MarketData;

namespace Trading.ArchitectureTests;

public sealed class MarketDataWorkerTests
{
    [Fact]
    public void BackfillSelectsRequiredSafeContiguousCompletedCandles()
    {
        var boundary = new DateTimeOffset(2026, 9, 22, 6, 0, 0, TimeSpan.Zero);
        var subscription = new CandleSubscription("BTC/EUR", CandleInterval.FiveMinutes);
        var candles = Enumerable.Range(0, PaperTrainingCandleBackfillService.RequiredCandleCount + 2)
            .Select(index =>
            {
                var open = boundary.AddMinutes(
                    (index - (PaperTrainingCandleBackfillService.RequiredCandleCount + 2)) * 5);
                return new Candle(
                    "BTC/EUR",
                    CandleInterval.FiveMinutes,
                    open,
                    open.AddMinutes(5),
                    100m,
                    101m,
                    99m,
                    100m,
                    10m,
                    true,
                    false);
            })
            .ToArray();

        var selected = PaperTrainingCandleBackfillService.SelectContiguousClosedHistory(
            candles,
            subscription,
            boundary);

        Assert.Equal(PaperTrainingCandleBackfillService.RequiredCandleCount, selected.Count);
        Assert.Equal(
            boundary.AddMinutes(-5 * PaperTrainingCandleBackfillService.RequiredCandleCount),
            selected[0].OpenTimeUtc);
        Assert.Equal(boundary, selected[^1].CloseTimeUtc);
    }

    [Fact]
    public void DisabledOrUnconfiguredWorkersProduceNoSubscriptions()
    {
        Assert.Empty(Worker.GetSubscriptions(new MarketDataStreamingOptions
        {
            Enabled = false,
            Symbols = ["BTC/USD"],
            Intervals = [CandleInterval.OneMinute],
        }));
        Assert.Empty(Worker.GetSubscriptions(new MarketDataStreamingOptions { Enabled = true }));
    }

    [Fact]
    public void EnabledWorkerNormalizesAndDeduplicatesConfiguredSubscriptions()
    {
        var subscriptions = Worker.GetSubscriptions(new MarketDataStreamingOptions
        {
            Enabled = true,
            Symbols = [" btc/usd ", "BTC/USD"],
            Intervals = [CandleInterval.OneMinute, CandleInterval.OneMinute, CandleInterval.None],
        });

        var subscription = Assert.Single(subscriptions);
        Assert.Equal("BTC/USD", subscription.Symbol);
        Assert.Equal(CandleInterval.OneMinute, subscription.Interval);
    }

    [Fact]
    public void ActivePaperIntervalsAreMergedWithoutCreatingACrossProduct()
    {
        var subscriptions = Worker.GetSubscriptions(
            new MarketDataStreamingOptions
            {
                Enabled = true,
                Symbols = ["BTC/USD"],
                Intervals = [CandleInterval.OneHour],
            },
            [
                new PaperTrainingMarketSubscription("ETH/USD", CandleInterval.FiveMinutes),
                new PaperTrainingMarketSubscription("btc/usd", CandleInterval.ThirtyMinutes),
                new PaperTrainingMarketSubscription("XRP/EUR", CandleInterval.OneDay)
            ]);

        Assert.Collection(
            subscriptions.OrderBy(subscription => subscription.Symbol).ThenBy(subscription => subscription.Interval),
            subscription =>
            {
                Assert.Equal("BTC/USD", subscription.Symbol);
                Assert.Equal(CandleInterval.ThirtyMinutes, subscription.Interval);
            },
            subscription =>
            {
                Assert.Equal("BTC/USD", subscription.Symbol);
                Assert.Equal(CandleInterval.OneHour, subscription.Interval);
            },
            subscription =>
            {
                Assert.Equal("ETH/USD", subscription.Symbol);
                Assert.Equal(CandleInterval.FiveMinutes, subscription.Interval);
            });
    }

    [Fact]
    public void TenMinuteIsNeverSentAsANativeKrakenSubscription()
    {
        var subscriptions = Worker.GetSubscriptions(new MarketDataStreamingOptions
        {
            Enabled = true,
            Symbols = ["BTC/USD"],
            Intervals = [CandleInterval.OneMinute, CandleInterval.TenMinutes],
            DeriveTenMinuteCandles = true,
        });

        var subscription = Assert.Single(subscriptions);
        Assert.Equal(CandleInterval.OneMinute, subscription.Interval);
    }

    [Fact]
    public void SubscriptionRefreshKeepsAStableStreamAndReconnectsOnlyForChanges()
    {
        var current = new[]
        {
            new CandleSubscription("XRP/EUR", CandleInterval.FiveMinutes),
            new CandleSubscription("XRP/EUR", CandleInterval.OneHour)
        };

        Assert.True(Worker.HaveSameSubscriptions(current, current.Reverse().ToArray()));
        Assert.False(Worker.HaveSameSubscriptions(
            current,
            [new CandleSubscription("XRP/EUR", CandleInterval.FiveMinutes)]));
    }

    [Fact]
    public async Task ProcessorPersistsExactlyOneDerivedTenMinuteCandleFromClosedContiguousMinutes()
    {
        var start = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryRepository();
        var processor = new CandleIngestionProcessor(
            repository,
            new AdvancingTimeProvider(start.AddMinutes(1)),
            NullLogger<CandleIngestionProcessor>.Instance,
            deriveTenMinuteCandles: true);

        foreach (var minute in Enumerable.Range(0, 10).Select(index => CreateMinute(start, index)))
        {
            Assert.Equal(CandleWriteResult.Inserted, await processor.ProcessAsync(minute, CancellationToken.None));
        }

        var derived = Assert.Single(await repository.ListAsync(
            "BTC/USD", CandleInterval.TenMinutes, start, start));
        Assert.True(derived.IsDerived);
        Assert.True(derived.CanBeUsedForClosedCandleSignal);
        Assert.Equal(100m, derived.Open);
        Assert.Equal(110m, derived.High);
        Assert.Equal(99m, derived.Low);
        Assert.Equal(109.5m, derived.Close);
        Assert.Equal(20m, derived.Volume);

        Assert.Equal(CandleWriteResult.Conflict,
            await processor.ProcessAsync(CreateMinute(start, 9), CancellationToken.None));
        Assert.Single(await repository.ListAsync("BTC/USD", CandleInterval.TenMinutes, start, start));
    }

    [Fact]
    public async Task ProcessorRefusesDerivedCandleWhenAConstituentIsMissing()
    {
        var start = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryRepository();
        var processor = new CandleIngestionProcessor(
            repository,
            new FixedTimeProvider(start.AddMinutes(11)),
            NullLogger<CandleIngestionProcessor>.Instance,
            deriveTenMinuteCandles: true);

        foreach (var index in Enumerable.Range(0, 10).Where(index => index != 5))
        {
            await processor.ProcessAsync(CreateMinute(start, index), CancellationToken.None);
        }

        Assert.Empty(await repository.ListAsync("BTC/USD", CandleInterval.TenMinutes, start, start));
    }

    [Fact]
    public async Task ProcessorRefusesDerivedCandleWhenAConstituentIsIncomplete()
    {
        var start = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        var repository = new InMemoryRepository();
        var processor = new CandleIngestionProcessor(
            repository,
            new FixedTimeProvider(start.AddMinutes(11)),
            NullLogger<CandleIngestionProcessor>.Instance,
            deriveTenMinuteCandles: true);

        foreach (var index in Enumerable.Range(0, 10))
        {
            var candle = CreateMinute(start, index);
            if (index == 5)
            {
                candle = new Candle(
                    candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                    candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, false, false);
            }

            await processor.ProcessAsync(candle, CancellationToken.None);
        }

        Assert.Empty(await repository.ListAsync("BTC/USD", CandleInterval.TenMinutes, start, start));
    }

    [Fact]
    public async Task ProcessorPersistsQualityEvidenceAndDoesNotOverwriteConflicts()
    {
        var repository = new RecordingRepository(CreateCandle(10));
        var processor = new CandleIngestionProcessor(
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<CandleIngestionProcessor>.Instance);

        var result = await processor.ProcessAsync(CreateCandle(9), CancellationToken.None);

        Assert.Equal(CandleWriteResult.Conflict, result);
        Assert.Contains(DataQualityIssue.OutOfOrder, repository.LastWritten!.QualityFlags);
        Assert.Contains(DataQualityIssue.Late, repository.LastWritten.QualityFlags);
        Assert.False(repository.LastWritten.CanBeUsedForClosedCandleSignal);
        Assert.Equal(1, repository.UpsertCalls);
    }

    [Fact]
    public async Task ProcessorMarksAHistoricalDuplicateBeforeRepositoryRejectsIt()
    {
        var existing = CreateCandle(10);
        var repository = new RecordingRepository(existing, existing);
        var processor = new CandleIngestionProcessor(
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<CandleIngestionProcessor>.Instance);

        var result = await processor.ProcessAsync(CreateCandle(10), CancellationToken.None);

        Assert.Equal(CandleWriteResult.Conflict, result);
        Assert.Contains(DataQualityIssue.Duplicate, repository.LastWritten!.QualityFlags);
        Assert.False(repository.LastWritten.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public void ReconnectDelayIsBoundedAndJittered()
    {
        var delay = Worker.GetReconnectDelay(20, 3);

        Assert.InRange(delay.TotalSeconds, 2.4d, 3d);
    }

    private static Candle CreateCandle(int hour) => new(
        "BTC/USD", CandleInterval.OneHour,
        new DateTimeOffset(2026, 9, 20, hour, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 20, hour + 1, 0, 0, TimeSpan.Zero),
        100m, 105m, 99m, 102m, 10m, true, false);

    private static Candle CreateMinute(DateTimeOffset start, int minute) => new(
        "BTC/USD", CandleInterval.OneMinute,
        start.AddMinutes(minute), start.AddMinutes(minute + 1),
        100m + minute, 101m + minute, 99m + minute, 100.5m + minute, 2m, true, false);

    private sealed class RecordingRepository : ICandleRepository
    {
        private readonly Candle _latest;
        private readonly IReadOnlyCollection<Candle> _sameOpenTime;

        internal RecordingRepository(Candle latest, params Candle[] sameOpenTime)
        {
            _latest = latest;
            _sameOpenTime = sameOpenTime;
        }
        internal Candle? LastWritten { get; private set; }
        internal int UpsertCalls { get; private set; }

        public Task<CandleWriteResult> UpsertAsync(Candle candle, CancellationToken cancellationToken = default)
        {
            LastWritten = candle;
            UpsertCalls++;
            return Task.FromResult(CandleWriteResult.Conflict);
        }

        public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken = default) =>
            Task.FromResult<Candle?>(_latest);

        public Task<IReadOnlyCollection<Candle>> ListAsync(string symbol, CandleInterval interval, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(fromUtc == toUtc ? _sameOpenTime : (IReadOnlyCollection<Candle>)Array.Empty<Candle>());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset first) : TimeProvider
    {
        private DateTimeOffset _next = first;

        public override DateTimeOffset GetUtcNow()
        {
            var current = _next;
            _next = _next.AddMinutes(1);
            return current;
        }
    }

    private sealed class InMemoryRepository : ICandleRepository
    {
        private readonly List<Candle> _candles = [];

        public Task<CandleWriteResult> UpsertAsync(Candle candle, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = _candles.SingleOrDefault(stored =>
                stored.Symbol == candle.Symbol &&
                stored.Interval == candle.Interval &&
                stored.OpenTimeUtc == candle.OpenTimeUtc);
            if (existing is null)
            {
                _candles.Add(candle);
                return Task.FromResult(CandleWriteResult.Inserted);
            }

            return Task.FromResult(existing.Open == candle.Open &&
                existing.High == candle.High &&
                existing.Low == candle.Low &&
                existing.Close == candle.Close &&
                existing.Volume == candle.Volume &&
                existing.IsClosed == candle.IsClosed &&
                existing.IsDerived == candle.IsDerived &&
                existing.QualityFlags.OrderBy(flag => flag).SequenceEqual(candle.QualityFlags.OrderBy(flag => flag))
                ? CandleWriteResult.Duplicate
                : CandleWriteResult.Conflict);
        }

        public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken = default) =>
            Task.FromResult(_candles.Where(candle => candle.Symbol == symbol && candle.Interval == interval)
                .OrderByDescending(candle => candle.CloseTimeUtc).ThenByDescending(candle => candle.OpenTimeUtc)
                .FirstOrDefault());

        public Task<IReadOnlyCollection<Candle>> ListAsync(
            string symbol, CandleInterval interval, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Candle>>(_candles.Where(candle =>
                    candle.Symbol == symbol && candle.Interval == interval &&
                    candle.OpenTimeUtc >= fromUtc && candle.OpenTimeUtc <= toUtc)
                .OrderBy(candle => candle.OpenTimeUtc).ToArray());
    }

}
