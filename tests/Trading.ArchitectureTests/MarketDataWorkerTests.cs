using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Workers.MarketData;

namespace Trading.ArchitectureTests;

public sealed class MarketDataWorkerTests
{
    [Fact]
    public async Task StreamRetryTelemetryExcludesExceptionMessages()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 1, 0, TimeSpan.Zero));
        using var services = new ServiceCollection()
            .AddSingleton<IPaperTrainingSubscriptionSource>(new DisabledPaperTrainingActivationSource())
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var backfill = new PaperTrainingCandleBackfillService(
            new CountingHistorySource([]), scopes, time,
            NullLogger<PaperTrainingCandleBackfillService>.Instance);
        var logger = new CapturingWorkerLogger();
        using var worker = new Worker(new FailingStreamingSource(), scopes, backfill,
            Options.Create(new MarketDataStreamingOptions
            {
                Enabled = true, Symbols = ["BTC/USD"], Intervals = [CandleInterval.FiveMinutes]
            }), logger);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 80
                && !logger.Contains("Market-data worker loop failed safely"); attempt++)
                await Task.Delay(25);
            Assert.True(logger.Contains("ErrorType=InvalidOperationException"));
            Assert.False(logger.Contains("sensitive-token-value"));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AcceptedForwardStreamCandlePublishesDurableFeedProof()
    {
        var close = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var now = close.AddMinutes(1);
        var candle = new Candle("BTC/USD", CandleInterval.FiveMinutes,
            close.AddMinutes(-5), close, 100m, 101m, 99m, 100m, 1m, true, false);
        var time = new MutableTimeProvider(now);
        var databaseName = Guid.NewGuid().ToString("N");
        using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<TradingDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .AddScoped<ICandleRepository, EfCandleRepository>()
            .AddScoped<CandleIngestionProcessor>()
            .AddSingleton<TimeProvider>(time)
            .AddSingleton<IPaperTrainingSubscriptionSource>(new DisabledPaperTrainingActivationSource())
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var history = new CountingHistorySource([]);
        var backfill = new PaperTrainingCandleBackfillService(
            history, scopes, time,
            NullLogger<PaperTrainingCandleBackfillService>.Instance);
        var workerLogger = new CapturingWorkerLogger();
        using var worker = new Worker(new SingleCandleStreamingSource(candle),
            scopes, backfill, Options.Create(new MarketDataStreamingOptions
            {
                Enabled = true, Symbols = ["BTC/USD"], Intervals = [CandleInterval.FiveMinutes],
                SubscriptionRefreshSeconds = 60
            }), workerLogger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var ready = false;
            for (var attempt = 0; attempt < 80 && !ready; attempt++)
            {
                using var scope = services.CreateScope();
                ready = await new EfPaperHostHeartbeatRepository(
                    scope.ServiceProvider.GetRequiredService<TradingDbContext>())
                    .HasRecentForwardCandleAsync(now)
                    && workerLogger.Contains("Paper forward closed candle persisted.");
                if (!ready)
                    await Task.Delay(25);
            }
            Assert.True(ready);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void BackfillSelectsRequiredSafeContiguousCompletedCandles()
    {
        Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, PaperTrainingCandleBackfillService.RequiredCandleCount);
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
        Assert.Empty(PaperTrainingCandleBackfillService.SelectContiguousClosedHistory(
            candles[..^1], subscription, boundary));
    }

    [Fact]
    public async Task RepeatedBackfillPersistsAClosedCandleWithoutAStreamTransition()
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var subscription = new CandleSubscription("BTC/USD", CandleInterval.FiveMinutes);
        var source = new CountingHistorySource(Window(boundary, subscription.Interval).Candles);
        var time = new MutableTimeProvider(boundary.AddSeconds(40));
        var databaseName = Guid.NewGuid().ToString();
        using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .AddScoped<EfCandleRepository>()
            .BuildServiceProvider();
        var backfill = new PaperTrainingCandleBackfillService(
            source, services.GetRequiredService<IServiceScopeFactory>(), time,
            NullLogger<PaperTrainingCandleBackfillService>.Instance);

        await backfill.EnsureAsync([subscription], CancellationToken.None);
        await backfill.EnsureAsync([subscription], CancellationToken.None);
        Assert.Equal(1, source.Fetches);

        time.Now = boundary.AddMinutes(5).AddSeconds(40);
        source.Candles = Window(boundary.AddMinutes(5), subscription.Interval).Candles;
        await backfill.EnsureAsync([subscription], CancellationToken.None);
        using var scope = services.CreateScope();
        var latest = await scope.ServiceProvider.GetRequiredService<EfCandleRepository>()
            .GetLatestAsync(subscription.Symbol, subscription.Interval);

        Assert.Equal(2, source.Fetches);
        Assert.NotNull(latest);
        Assert.Equal(boundary, latest.OpenTimeUtc);
        Assert.True(latest.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public async Task StableStreamRefreshPersistsTheNextRestClosedCandle()
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var source = new CountingHistorySource(Window(boundary, CandleInterval.FiveMinutes).Candles);
        var time = new MutableTimeProvider(boundary.AddSeconds(40));
        var databaseName = Guid.NewGuid().ToString();
        using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .AddScoped<EfCandleRepository>()
            .AddSingleton<IPaperTrainingSubscriptionSource>(new DisabledPaperTrainingActivationSource())
            .BuildServiceProvider();
        var backfill = new PaperTrainingCandleBackfillService(
            source, services.GetRequiredService<IServiceScopeFactory>(), time,
            NullLogger<PaperTrainingCandleBackfillService>.Instance);
        var worker = new Worker(
            new IdleStreamingSource(), services.GetRequiredService<IServiceScopeFactory>(),
            backfill, Options.Create(new MarketDataStreamingOptions
            {
                Enabled = true,
                Symbols = ["BTC/USD"],
                Intervals = [CandleInterval.FiveMinutes],
                SubscriptionRefreshSeconds = 1
            }), NullLogger<Worker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => source.Fetches >= 1);
            source.Candles = Window(boundary.AddMinutes(5), CandleInterval.FiveMinutes).Candles;
            time.Now = boundary.AddMinutes(5).AddSeconds(40);
            await WaitForAsync(() => source.Fetches >= 2);

            Candle? latest = null;
            for (var attempt = 0; attempt < 80 && latest?.OpenTimeUtc != boundary; attempt++)
            {
                using var scope = services.CreateScope();
                latest = await scope.ServiceProvider.GetRequiredService<EfCandleRepository>()
                    .GetLatestAsync("BTC/USD", CandleInterval.FiveMinutes);
                if (latest?.OpenTimeUtc != boundary)
                    await Task.Delay(25);
            }
            Assert.NotNull(latest);
            Assert.Equal(boundary, latest.OpenTimeUtc);
            Assert.True(latest.CanBeUsedForClosedCandleSignal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("The expected historical fetch did not occur.");
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
            },
            subscription =>
            {
                Assert.Equal("XRP/EUR", subscription.Symbol);
                Assert.Equal(CandleInterval.OneDay, subscription.Interval);
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
    public void ScannerWaitsForRestSettlementBeforeRecordingAClosedBoundary()
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        Assert.False(ContinuousPaperScannerWorker.BoundaryHasSettled(boundary));
        Assert.False(ContinuousPaperScannerWorker.BoundaryHasSettled(
            boundary.Add(ContinuousPaperScannerWorker.RestSettlementGrace).AddTicks(-1)));
        Assert.True(ContinuousPaperScannerWorker.BoundaryHasSettled(
            boundary.Add(ContinuousPaperScannerWorker.RestSettlementGrace)));
        Assert.True(ContinuousPaperScannerWorker.BoundaryHasSettled(boundary.AddMinutes(4)));
        Assert.False(ContinuousPaperScannerWorker.BoundaryHasSettled(boundary.AddMinutes(5)));
        Assert.True(ContinuousPaperScannerWorker.BoundaryHasSettled(
            boundary.AddSeconds(30).ToOffset(TimeSpan.FromHours(2))));
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

        Assert.Equal(CandleWriteResult.Duplicate,
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
    public async Task ProcessorTreatsAnIdenticalHistoricalReplayAsIdempotent()
    {
        var existing = CreateCandle(10);
        var repository = new RecordingRepository(existing, existing);
        var processor = new CandleIngestionProcessor(
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<CandleIngestionProcessor>.Instance);

        var result = await processor.ProcessAsync(CreateCandle(10), CancellationToken.None);

        Assert.Equal(CandleWriteResult.Duplicate, result);
        Assert.Null(repository.LastWritten);
        Assert.Equal(0, repository.UpsertCalls);
    }

    [Fact]
    public async Task ProcessorRetainsAConflictingHistoricalReplayForInvestigation()
    {
        var existing = CreateCandle(10);
        var repository = new RecordingRepository(existing, existing);
        var processor = new CandleIngestionProcessor(
            repository,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<CandleIngestionProcessor>.Instance);
        var changed = new Candle(
            existing.Symbol, existing.Interval, existing.OpenTimeUtc, existing.CloseTimeUtc,
            existing.Open, existing.High, existing.Low, existing.Close + 1m, existing.Volume,
            existing.IsClosed, existing.IsDerived);

        var result = await processor.ProcessAsync(changed, CancellationToken.None);

        Assert.Equal(CandleWriteResult.Conflict, result);
        Assert.Contains(DataQualityIssue.Duplicate, repository.LastWritten!.QualityFlags);
        Assert.False(repository.LastWritten.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public async Task AdmissionStagesClosedSignalAndProtectiveCandlesBeforeWorkerRuns()
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repository = new EfCandleRepository(db);
        var signal = Window(boundary, CandleInterval.FiveMinutes);
        var protective = Window(boundary, CandleInterval.OneMinute);
        var stager = new DurablePaperScanEvidenceStager(
            new FixedHistorySource(protective.Candles), repository);

        Assert.True(await stager.StageAsync("BTC/USD", boundary, [signal]));
        Assert.True(await stager.StageAsync("BTC/USD", boundary, [signal]));
        Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, (await repository.ListAsync("BTC/USD", CandleInterval.FiveMinutes,
            signal.Candles[0].OpenTimeUtc, boundary)).Count);
        Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, (await repository.ListAsync("BTC/USD", CandleInterval.OneMinute,
            protective.Candles[0].OpenTimeUtc, boundary)).Count);
    }

    [Fact]
    public async Task AdmissionRejectsConflictingDurableEvidenceInsteadOfOverwritingIt()
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var repository = new EfCandleRepository(db);
        var signal = Window(boundary, CandleInterval.FiveMinutes);
        var first = signal.Candles[0];
        await repository.UpsertAsync(new Candle(first.Symbol, first.Interval,
            first.OpenTimeUtc, first.CloseTimeUtc, first.Open, first.High,
            first.Low, first.Close + 1m, first.Volume, true, false));
        var stager = new DurablePaperScanEvidenceStager(
            new FixedHistorySource(Window(boundary, CandleInterval.OneMinute).Candles), repository);

        Assert.False(await stager.StageAsync("BTC/USD", boundary, [signal]));
        Assert.Equal(first.Close + 1m, (await repository.GetLatestAsync(
            "BTC/USD", CandleInterval.FiveMinutes))!.Close);
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

    private static ExperimentCandleSeries Window(DateTimeOffset boundary, CandleInterval interval)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var candles = Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
            .Select(index =>
            {
                var open = boundary - TimeSpan.FromTicks(
                    duration.Ticks * (ApprovedConsensusStrategyProfiles.RequiredHistory - index));
                return new Candle("BTC/USD", interval, open, open + duration,
                    100m, 101m, 99m, 100m, 2m, true, false);
            }).ToArray();
        return new ExperimentCandleSeries("BTC/USD", interval, boundary, candles);
    }

    private sealed class FixedHistorySource(IReadOnlyList<Candle> candles) : IHistoricalCandleSource
    {
        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(candles);
    }

    private sealed class CountingHistorySource(IReadOnlyList<Candle> candles) : IHistoricalCandleSource
    {
        private int _fetches;
        internal IReadOnlyList<Candle> Candles { get; set; } = candles;
        internal int Fetches => Volatile.Read(ref _fetches);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _fetches);
            return Task.FromResult(Candles);
        }
    }

    private sealed class IdleStreamingSource : IStreamingCandleSource
    {
        public async IAsyncEnumerable<Candle> StreamAsync(
            IReadOnlyCollection<CandleSubscription> subscriptions,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class SingleCandleStreamingSource(Candle candle) : IStreamingCandleSource
    {
        public async IAsyncEnumerable<Candle> StreamAsync(
            IReadOnlyCollection<CandleSubscription> subscriptions,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return candle;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingStreamingSource : IStreamingCandleSource
    {
        public async IAsyncEnumerable<Candle> StreamAsync(
            IReadOnlyCollection<CandleSubscription> subscriptions,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested)
                yield break;
            throw new InvalidOperationException("sensitive-token-value");
        }
    }

    private sealed class CapturingWorkerLogger : ILogger<Worker>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public bool Contains(string message) =>
            _messages.Any(entry => entry.Contains(message, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullLogger<Worker>.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

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
