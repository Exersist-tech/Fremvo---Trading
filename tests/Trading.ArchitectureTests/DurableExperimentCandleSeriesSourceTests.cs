using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Workers.Experiments;

namespace Trading.ArchitectureTests;

public sealed class DurableExperimentCandleSeriesSourceTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReturnsAnImmutableChronologicalClosedSnapshotWithDurableProvenanceAsync()
    {
        var repository = new RecordingRepository(
            Candle(9),
            Candle(10),
            Candle(11));
        var source = Source(repository);

        var result = await source.GetClosedSeriesAsync(Request(3));

        Assert.True(result.IsAvailable);
        Assert.Equal(ExperimentCandleSeriesBlockReason.None, result.BlockReason);
        var series = Assert.IsType<ExperimentCandleSeries>(result.Series);
        Assert.Equal("BTC/USD", series.Symbol);
        Assert.Equal(CandleInterval.OneHour, series.Interval);
        Assert.Equal(AsOf, series.AsOfUtc);
        Assert.Equal(ExperimentCandleSeries.DurableCandleRepositoryProvenance, series.DatasetProvenance);
        Assert.Equal(new[] { At(9), At(10), At(11) }, series.Candles.Select(candle => candle.OpenTimeUtc));
        Assert.All(series.Candles, candle => Assert.True(candle.CanBeUsedForClosedCandleSignal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DurableExperimentCandleSeriesSource.MaximumRequestedCount + 1)]
    public async Task RejectsUnboundedOrInvalidCountAsync(int maximumCount)
    {
        var repository = new RecordingRepository(Candle(11));

        var result = await Source(repository).GetClosedSeriesAsync(Request(maximumCount));

        Assert.Equal(ExperimentCandleSeriesBlockReason.InvalidRequest, result.BlockReason);
        Assert.False(repository.WasRead);
    }

    [Fact]
    public async Task UsesAnExplicitUtcAsOfAndBoundedRepositoryWindowAsync()
    {
        var repository = new RecordingRepository(Candle(10), Candle(11));
        var source = Source(repository);

        var result = await source.GetClosedSeriesAsync(Request(2));

        Assert.True(result.IsAvailable);
        Assert.Equal(At(9), repository.FromUtc);
        Assert.Equal(At(11), repository.ToUtc);
        Assert.Equal("BTC/USD", repository.Symbol);
        Assert.Equal(CandleInterval.OneHour, repository.Interval);

        result = await source.GetClosedSeriesAsync(
            new ExperimentCandleSeriesRequest("BTC/USD", CandleInterval.OneHour, AsOf.ToOffset(TimeSpan.FromHours(2)), 2));

        Assert.Equal(ExperimentCandleSeriesBlockReason.InvalidRequest, result.BlockReason);
    }

    [Fact]
    public async Task UnalignedEvaluationTimeStillRequestsTheFullClosedCandleWindowAsync()
    {
        var repository = new RecordingRepository(Candle(9), Candle(10), Candle(11));
        var source = Source(repository);
        var unalignedAsOf = AsOf.AddMinutes(37);

        var result = await source.GetClosedSeriesAsync(
            new ExperimentCandleSeriesRequest("BTC/USD", CandleInterval.OneHour, unalignedAsOf, 3));

        Assert.True(result.IsAvailable);
        Assert.Equal(At(8), repository.FromUtc);
        Assert.Equal(At(11), repository.ToUtc);
        Assert.Equal(3, result.Series!.Candles.Count);
    }

    [Fact]
    public async Task BlocksNoDataAsync() =>
        await AssertBlockedAsync(Array.Empty<Candle>(), ExperimentCandleSeriesBlockReason.NoData);

    [Fact]
    public async Task BlocksRepositoryResultsExceedingRequestedMaximumAsync() =>
        await AssertBlockedAsync(new[] { Candle(7), Candle(8), Candle(9), Candle(10), Candle(11) },
            ExperimentCandleSeriesBlockReason.MaximumCountExceeded, maximumCount: 3);

    [Fact]
    public async Task BlocksFutureCandlesInsteadOfDiscardingThemAsync() =>
        await AssertBlockedAsync(new[] { Candle(11), Candle(12) }, ExperimentCandleSeriesBlockReason.FutureCandle);

    [Fact]
    public async Task BlocksWrongSymbolOrIntervalAsync() =>
        await AssertBlockedAsync(new[] { Candle(11, symbol: "ETH/USD") },
            ExperimentCandleSeriesBlockReason.WrongSymbolOrInterval);

    [Fact]
    public async Task BlocksDuplicateAndOutOfOrderCandlesAsync()
    {
        await AssertBlockedAsync(new[] { Candle(10), Candle(10) },
            ExperimentCandleSeriesBlockReason.DuplicateOrOutOfOrder);
        await AssertBlockedAsync(new[] { Candle(11), Candle(10) },
            ExperimentCandleSeriesBlockReason.DuplicateOrOutOfOrder);
    }

    [Fact]
    public async Task BlocksGapsAsync() =>
        await AssertBlockedAsync(new[] { Candle(9), Candle(11) }, ExperimentCandleSeriesBlockReason.Gap);

    [Fact]
    public async Task BlocksUnsafeOrFormingCandlesAsync() =>
        await AssertBlockedAsync(new[] { Candle(11, isClosed: false) },
            ExperimentCandleSeriesBlockReason.UnsafeCandle);

    [Fact]
    public async Task BlocksStaleLatestCandleAsync() =>
        await AssertBlockedAsync(new[] { Candle(8) }, ExperimentCandleSeriesBlockReason.Stale);

    [Fact]
    public async Task ScalesFreshnessToTheRequestedIntervalAsync()
    {
        var staleFiveMinuteCandle = new Candle(
            "BTC/USD",
            CandleInterval.FiveMinutes,
            AsOf.AddMinutes(-20),
            AsOf.AddMinutes(-15),
            100m,
            101m,
            99m,
            100m,
            1m,
            true,
            false);

        var result = await Source(new RecordingRepository(staleFiveMinuteCandle))
            .GetClosedSeriesAsync(new("BTC/USD", CandleInterval.FiveMinutes, AsOf, 3));

        Assert.False(result.IsAvailable);
        Assert.Equal(ExperimentCandleSeriesBlockReason.Stale, result.BlockReason);
    }

    [Fact]
    public void SourceHasNoExchangeOrSecretDependency()
    {
        var references = typeof(DurableExperimentCandleSeriesSource).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain(references, name => name!.Contains("Kraken", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name!.Contains("Secrets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerDefaultCandleSourceIsInertAsync()
    {
        var source = new UnconfiguredExperimentCandleSeriesSource();

        var result = await source.GetClosedSeriesAsync(Request(1));

        Assert.False(result.IsAvailable);
        Assert.Equal(ExperimentCandleSeriesBlockReason.NoData, result.BlockReason);
    }

    private static DurableExperimentCandleSeriesSource Source(RecordingRepository repository) =>
        new(repository, new ExperimentCandleFreshnessPolicy(TimeSpan.FromHours(2)));

    private static async Task AssertBlockedAsync(
        IReadOnlyCollection<Candle> candles,
        ExperimentCandleSeriesBlockReason reason,
        int maximumCount = 3)
    {
        var result = await Source(new RecordingRepository(candles)).GetClosedSeriesAsync(Request(maximumCount));
        Assert.False(result.IsAvailable);
        Assert.Null(result.Series);
        Assert.Equal(reason, result.BlockReason);
    }

    private static ExperimentCandleSeriesRequest Request(int maximumCount) =>
        new("BTC/USD", CandleInterval.OneHour, AsOf, maximumCount);

    private static Candle Candle(int openHour, string symbol = "BTC/USD", bool isClosed = true) =>
        new(
            symbol,
            CandleInterval.OneHour,
            At(openHour),
            At(openHour + 1),
            100m,
            101m,
            99m,
            100m,
            1m,
            isClosed,
            isDerived: false);

    private static DateTimeOffset At(int hour) =>
        new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero).AddHours(hour);

    private sealed class RecordingRepository : ICandleRepository
    {
        private readonly IReadOnlyCollection<Candle> _candles;

        public RecordingRepository(params Candle[] candles)
            : this((IReadOnlyCollection<Candle>)candles)
        {
        }

        public RecordingRepository(IReadOnlyCollection<Candle> candles) => _candles = candles;

        public bool WasRead { get; private set; }
        public string? Symbol { get; private set; }
        public CandleInterval Interval { get; private set; }
        public DateTimeOffset FromUtc { get; private set; }
        public DateTimeOffset ToUtc { get; private set; }

        public Task<CandleWriteResult> UpsertAsync(Candle candle, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Candle?> GetLatestAsync(string symbol, CandleInterval interval, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Candle>> ListAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default)
        {
            WasRead = true;
            Symbol = symbol;
            Interval = interval;
            FromUtc = fromUtc;
            ToUtc = toUtc;
            return Task.FromResult(_candles);
        }
    }
}
