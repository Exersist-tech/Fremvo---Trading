using Microsoft.Extensions.Logging.Abstractions;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Workers.MarketData;

namespace Trading.ArchitectureTests;

public sealed class MarketDataWorkerTests
{
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

}
