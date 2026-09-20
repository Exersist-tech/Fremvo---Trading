using Microsoft.EntityFrameworkCore;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class EfCandleRepositoryTests
{
    [Fact]
    public async Task UpsertRoundTripsDecimalsAndQualityFlagsAsync()
    {
        await using var context = CreateContext();
        var repository = new EfCandleRepository(context);
        var candle = CreateCandle(
            openTimeUtc: At(10),
            open: 12345.123456789012m,
            qualityFlags: new[] { DataQualityIssue.Derived, DataQualityIssue.Late });

        Assert.Equal(CandleWriteResult.Inserted, await repository.UpsertAsync(candle));

        var stored = await repository.GetLatestAsync(candle.Symbol, candle.Interval);

        Assert.NotNull(stored);
        Assert.Equal(candle.Open, stored!.Open);
        Assert.Equal(candle.High, stored.High);
        Assert.Equal(candle.Low, stored.Low);
        Assert.Equal(candle.Close, stored.Close);
        Assert.Equal(candle.Volume, stored.Volume);
        Assert.Equal(candle.QualityFlags.OrderBy(flag => flag), stored.QualityFlags.OrderBy(flag => flag));
        Assert.True(stored.IsClosed);
        Assert.True(stored.IsDerived);
    }

    [Fact]
    public async Task DerivedTenMinuteCandlePersistsWithConstituentQualityEvidenceAsync()
    {
        await using var context = CreateContext();
        var repository = new EfCandleRepository(context);
        var start = new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);
        var constituents = Enumerable.Range(0, 10).Select(index => new Candle(
            "BTC/USD", CandleInterval.OneMinute,
            start.AddMinutes(index), start.AddMinutes(index + 1),
            100m + index, 101m + index, 99m + index, 100.5m + index, 2m,
            isClosed: true, isDerived: false,
            index == 4 ? new[] { DataQualityIssue.Late } : null)).ToArray();
        var derived = DerivedCandleBuilder.BuildTenMinuteCandle(
            constituents, "BTC/USD", start, start.AddMinutes(10), isClosed: true);

        Assert.Equal(CandleWriteResult.Inserted, await repository.UpsertAsync(derived));

        var persisted = Assert.Single(await repository.ListAsync(
            "BTC/USD", CandleInterval.TenMinutes, start, start));
        Assert.True(persisted.IsDerived);
        Assert.Contains(DataQualityIssue.Late, persisted.QualityFlags);
        Assert.Contains(DataQualityIssue.Derived, persisted.QualityFlags);
        Assert.False(persisted.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public async Task ListIsUtcBoundedAndChronologicalAsync()
    {
        await using var context = CreateContext();
        var repository = new EfCandleRepository(context);
        var first = CreateCandle(At(8));
        var second = CreateCandle(At(9));
        var third = CreateCandle(At(10));

        await repository.UpsertAsync(third);
        await repository.UpsertAsync(first);
        await repository.UpsertAsync(second);

        var result = await repository.ListAsync(
            first.Symbol,
            first.Interval,
            At(8).AddMinutes(30),
            At(10));

        Assert.Equal(new[] { second.OpenTimeUtc, third.OpenTimeUtc }, result.Select(candle => candle.OpenTimeUtc));
    }

    [Fact]
    public async Task GetLatestUsesCloseTimeThenOpenTimeDeterministicallyAsync()
    {
        await using var context = CreateContext();
        var repository = new EfCandleRepository(context);
        var earliestOpen = CreateCandle(
            At(8),
            closeTimeUtc: At(11));
        var laterOpen = CreateCandle(
            At(9),
            closeTimeUtc: At(11));

        await repository.UpsertAsync(earliestOpen);
        await repository.UpsertAsync(laterOpen);

        var latest = await repository.GetLatestAsync(earliestOpen.Symbol, earliestOpen.Interval);

        Assert.Equal(laterOpen.OpenTimeUtc, latest!.OpenTimeUtc);
    }

    [Fact]
    public async Task UpsertTreatsExactEvidenceAsIdempotentAndRejectsConflictingEvidenceAsync()
    {
        await using var context = CreateContext();
        var repository = new EfCandleRepository(context);
        var candle = CreateCandle(At(10));
        var conflicting = CreateCandle(
            candle.OpenTimeUtc,
            close: candle.Close + 1m);

        Assert.Equal(CandleWriteResult.Inserted, await repository.UpsertAsync(candle));
        Assert.Equal(CandleWriteResult.Duplicate, await repository.UpsertAsync(candle));
        Assert.Equal(CandleWriteResult.Conflict, await repository.UpsertAsync(conflicting));

        var stored = await repository.GetLatestAsync(candle.Symbol, candle.Interval);
        Assert.Equal(candle.Close, stored!.Close);
        Assert.Single(context.Candles);
    }

    [Fact]
    public async Task RepositoryPropagatesCancellationAsync()
    {
        await using var context = CreateContext();
        var repository = new EfCandleRepository(context);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.UpsertAsync(CreateCandle(At(10)), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => repository.GetLatestAsync("BTC/USD", CandleInterval.OneHour, cancellation.Token));
    }

    [Fact]
    public void CandleMappingUsesCompositeKeyDecimalColumnsAndLatestIndex()
    {
        using var context = CreateRelationalContext();
        var entity = context.Model.FindEntityType(typeof(PersistedCandle));

        Assert.NotNull(entity);
        Assert.Equal(
            new[] { nameof(PersistedCandle.Symbol), nameof(PersistedCandle.Interval), nameof(PersistedCandle.OpenTimeUtc) },
            entity!.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.All(
            new[]
            {
                nameof(PersistedCandle.Open),
                nameof(PersistedCandle.High),
                nameof(PersistedCandle.Low),
                nameof(PersistedCandle.Close),
                nameof(PersistedCandle.Volume)
            },
            propertyName => Assert.Equal("decimal(28,12)", entity.FindProperty(propertyName)!.GetColumnType()));
        Assert.Contains(
            entity.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(
                new[]
                {
                    nameof(PersistedCandle.Symbol),
                    nameof(PersistedCandle.Interval),
                    nameof(PersistedCandle.CloseTimeUtc),
                    nameof(PersistedCandle.OpenTimeUtc)
                }));
    }

    private static TradingDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

    private static TradingDbContext CreateRelationalContext() =>
        new(
            new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TradingCandleMappingTests;Trusted_Connection=True")
                .Options);

    private static DateTimeOffset At(int hour) => new(2026, 9, 20, hour, 0, 0, TimeSpan.Zero);

    private static Candle CreateCandle(
        DateTimeOffset openTimeUtc,
        DateTimeOffset? closeTimeUtc = null,
        decimal open = 101.123456789012m,
        decimal? close = null,
        IReadOnlyCollection<DataQualityIssue>? qualityFlags = null) =>
        new(
            "BTC/USD",
            CandleInterval.OneHour,
            openTimeUtc,
            closeTimeUtc ?? openTimeUtc.AddHours(1),
            open,
            105.123456789012m,
            99.123456789012m,
            close ?? 103.123456789012m,
            456.123456789012m,
            isClosed: true,
            isDerived: true,
            qualityFlags);
}
