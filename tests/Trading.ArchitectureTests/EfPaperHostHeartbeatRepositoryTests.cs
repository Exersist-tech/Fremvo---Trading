using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class EfPaperHostHeartbeatRepositoryTests
{
    [Fact]
    public async Task BothPaperHostServicesPublishTheirHeartbeatWhenStarted()
    {
        var database = Guid.NewGuid().ToString("N");
        using var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseInMemoryDatabase(database))
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var scannerLogger = new CapturingLogger<Trading.Workers.MarketData.PaperHostHeartbeatWorker>();
        var experimentLogger = new CapturingLogger<Trading.Workers.Experiments.PaperHostHeartbeatWorker>();
        using var scanner = new Trading.Workers.MarketData.PaperHostHeartbeatWorker(
            scopes, TimeProvider.System, scannerLogger);
        using var experiments = new Trading.Workers.Experiments.PaperHostHeartbeatWorker(
            scopes, TimeProvider.System, experimentLogger);
        await scanner.StartAsync(CancellationToken.None);
        await experiments.StartAsync(CancellationToken.None);
        try
        {
            DateTimeOffset? scannerAt = null;
            DateTimeOffset? experimentsAt = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                using var scope = scopes.CreateScope();
                var heartbeat = new EfPaperHostHeartbeatRepository(
                    scope.ServiceProvider.GetRequiredService<TradingDbContext>());
                scannerAt = await heartbeat.LastAsync(EfPaperHostHeartbeatRepository.Scanner);
                experimentsAt = await heartbeat.LastAsync(EfPaperHostHeartbeatRepository.Experiments);
                if (scannerAt.HasValue && experimentsAt.HasValue
                    && scannerLogger.Contains("Paper market-data host heartbeat persisted.")
                    && experimentLogger.Contains("Paper experiment host heartbeat persisted."))
                    break;
                await Task.Delay(20);
            }
            Assert.NotNull(scannerAt);
            Assert.NotNull(experimentsAt);
            Assert.True(scannerLogger.Contains("Paper market-data host heartbeat persisted."));
            Assert.True(experimentLogger.Contains("Paper experiment host heartbeat persisted."));
        }
        finally
        {
            await scanner.StopAsync(CancellationToken.None);
            await experiments.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PersistsDistinctScannerAndExperimentHostLivenessAcrossContexts()
    {
        var database = Guid.NewGuid().ToString("N");
        var time = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await using (var db = Context(database))
        {
            var heartbeat = new EfPaperHostHeartbeatRepository(db);
            await heartbeat.RecordAsync(EfPaperHostHeartbeatRepository.Scanner, time);
            await heartbeat.RecordAsync(EfPaperHostHeartbeatRepository.Experiments, time.AddSeconds(20));
            await heartbeat.RecordAsync(EfPaperHostHeartbeatRepository.Scanner, time.AddMinutes(1));
        }
        await using (var db = Context(database))
        {
            var heartbeat = new EfPaperHostHeartbeatRepository(db);
            Assert.Equal(time.AddMinutes(1), await heartbeat.LastAsync(EfPaperHostHeartbeatRepository.Scanner));
            Assert.Equal(time.AddSeconds(20), await heartbeat.LastAsync(EfPaperHostHeartbeatRepository.Experiments));
            await Assert.ThrowsAsync<ArgumentException>(() => heartbeat.RecordAsync("unknown", time));
            await Assert.ThrowsAsync<ArgumentException>(() => heartbeat.RecordAsync(
                EfPaperHostHeartbeatRepository.Scanner, time.ToOffset(TimeSpan.FromHours(1))));
        }
    }

    [Fact]
    public async Task PaperStartReadinessRequiresTwoCurrentUtcHostHeartbeats()
    {
        var now = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        await using var db = Context(Guid.NewGuid().ToString("N"));
        var heartbeat = new EfPaperHostHeartbeatRepository(db);
        Assert.False(await heartbeat.BothFreshAsync(now));
        await heartbeat.RecordAsync(EfPaperHostHeartbeatRepository.Scanner, now.AddMinutes(-2));
        Assert.False(await heartbeat.BothFreshAsync(now));
        await heartbeat.RecordAsync(EfPaperHostHeartbeatRepository.Experiments, now);
        Assert.True(await heartbeat.BothFreshAsync(now));
        Assert.False(await heartbeat.BothFreshAsync(now.AddTicks(1)));
        Assert.False(await heartbeat.BothFreshAsync(now.AddSeconds(-1)));
        await Assert.ThrowsAsync<ArgumentException>(() => heartbeat.BothFreshAsync(
            now.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public async Task ForwardFeedReadinessRequiresRecentSafeClosedStreamEvidenceAcrossContexts()
    {
        var database = Guid.NewGuid().ToString("N");
        var close = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var candle = new Candle("BTC/USD", CandleInterval.FiveMinutes,
            close.AddMinutes(-5), close, 100m, 101m, 99m, 100m, 1m, true, false);
        var observed = close.AddMinutes(1);
        await using (var db = Context(database))
        {
            var evidence = new EfPaperHostHeartbeatRepository(db);
            Assert.False(await evidence.HasRecentForwardCandleAsync(observed));
            await evidence.RecordForwardCandleAsync(candle, observed);
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.HasRecentForwardCandleAsync(
                DateTimeOffset.MinValue));
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.RecordForwardCandleAsync(
                candle, observed.ToOffset(TimeSpan.FromHours(1))));
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.RecordForwardCandleAsync(
                candle, close.AddMinutes(11)));
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.RecordForwardCandleAsync(
                candle, close.AddMinutes(-1)));
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.RecordForwardCandleAsync(
                new Candle("BTC/USD", CandleInterval.FiveMinutes,
                    candle.OpenTimeUtc, close, 100m, 101m, 99m, 100m, 1m, false, false), observed));
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.RecordForwardCandleAsync(
                new Candle("BTC/USD", CandleInterval.OneHour,
                    close.AddHours(-1), close, 100m, 101m, 99m, 100m, 1m, true, false), observed));
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.RecordForwardCandleAsync(
                new Candle("BTC/USD", CandleInterval.FiveMinutes,
                    close.AddMinutes(-5), close, 0m, 101m, 0m, 100m, 1m, true, false), observed));
            await Assert.ThrowsAsync<ArgumentException>(() => evidence.RecordForwardCandleAsync(
                new Candle("BTC/USD", CandleInterval.FiveMinutes,
                    close.AddMinutes(-4), close, 100m, 101m, 99m, 100m, 1m, true, false), observed));
        }
        await using (var db = Context(database))
        {
            var evidence = new EfPaperHostHeartbeatRepository(db);
            Assert.True(await evidence.HasRecentForwardCandleAsync(observed.AddMinutes(1)));
            Assert.False(await evidence.HasRecentForwardCandleAsync(close.AddMinutes(11)));
            Assert.False(await evidence.HasRecentForwardCandleAsync(close.AddMinutes(-1)));
        }
    }

    private static TradingDbContext Context(string database) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(database).Options);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public bool Contains(string message) => _messages.Contains(message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            NullLogger<T>.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            _messages.Enqueue(formatter(state, exception));
    }
}
