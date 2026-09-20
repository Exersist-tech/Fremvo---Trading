using Microsoft.EntityFrameworkCore;
using Trading.Backtesting;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Backtesting;

namespace Trading.ArchitectureTests;

public sealed class HistoricalDatasetRepositoryTests
{
    private static readonly string[] ExpectedOrderedIds = ["first", "second"];

    [Fact]
    public void ManifestNormalizesUtcAndRejectsUnstableOrInvalidEvidence()
    {
        var created = DateTimeOffset.UtcNow.AddHours(-1);
        var alignedUtc = new DateTimeOffset(
            created.Year, created.Month, created.Day, created.Hour, 0, 0, TimeSpan.Zero);
        var offset = alignedUtc.AddHours(-1).ToOffset(TimeSpan.FromHours(2));
        var dataset = CreateDataset(createdAtUtc: created, fromUtc: offset, toUtc: offset);

        Assert.Equal(TimeSpan.Zero, dataset.FromUtc.Offset);
        Assert.Equal(TimeSpan.Zero, dataset.ToUtc.Offset);
        Assert.True(dataset.ContainsOnlyClosedCandles);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDataset(candleCount: 0));
        Assert.Throws<ArgumentException>(() => CreateDataset(contentFingerprint: "not-a-sha256"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDataset(interval: "7m"));
        Assert.Throws<ArgumentException>(() => CreateDataset(toUtc: DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void VersionIdentityIsImmutableAndChangesWithReproducibilityContract()
    {
        var original = CreateDataset();
        var exactReplay = CreateDataset();
        var changedSourceVersion = CreateDataset(sourceVersion: "archive-v2");

        Assert.Equal(original.VersionIdentity, exactReplay.VersionIdentity);
        Assert.NotEqual(original.VersionIdentity, changedSourceVersion.VersionIdentity);
        Assert.All(typeof(HistoricalDataset).GetProperties(), property => Assert.False(property.CanWrite));
    }

    [Fact]
    public async Task StoreIsIdempotentAndRejectsConflictingRequestForSameVersionAsync()
    {
        await using var context = CreateContext();
        var repository = new EfHistoricalDatasetRepository(context);
        var dataset = CreateDataset();
        var conflictingRequest = CreateDataset(id: "another-request");

        Assert.Equal(HistoricalDatasetWriteResult.Inserted, await repository.StoreAsync(dataset));
        Assert.Equal(HistoricalDatasetWriteResult.Duplicate, await repository.StoreAsync(CreateDataset()));
        Assert.Equal(HistoricalDatasetWriteResult.Conflict, await repository.StoreAsync(conflictingRequest));
        Assert.Single(context.HistoricalDatasets);
    }

    [Fact]
    public async Task ListsContainedDatasetsInStableOrderAndDoesNotExposeMutableTrackingAsync()
    {
        await using var context = CreateContext();
        var repository = new EfHistoricalDatasetRepository(context);
        var first = CreateDataset(id: "first", fromUtc: At(0), toUtc: At(0), createdAtUtc: At(3));
        var second = CreateDataset(id: "second", fromUtc: At(1), toUtc: At(1), createdAtUtc: At(3));
        await repository.StoreAsync(second);
        await repository.StoreAsync(first);

        var results = await repository.ListAsync("BTC/USD", "1h", At(0), At(1), 10);

        Assert.Equal(ExpectedOrderedIds, results.Select(dataset => dataset.Id));
        Assert.All(results, dataset => Assert.True(dataset.ContainsOnlyClosedCandles));
    }

    [Fact]
    public async Task ContextRejectsUpdatesAndDeletesOfPersistedManifestsAsync()
    {
        await using var context = CreateContext();
        var repository = new EfHistoricalDatasetRepository(context);
        var dataset = CreateDataset();
        await repository.StoreAsync(dataset);
        var stored = await context.HistoricalDatasets.SingleAsync();
        stored.Symbol = "ETH/USD";

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        context.Entry(stored).State = EntityState.Unchanged;
        context.HistoricalDatasets.Remove(stored);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public void MappingUsesContentAddressedKeyAndNoOwnershipOrSecretColumns()
    {
        using var context = new TradingDbContext(
            new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TradingHistoricalDatasetMappingTests;Trusted_Connection=True")
                .Options);
        var entity = context.Model.FindEntityType(typeof(PersistedHistoricalDataset));

        Assert.NotNull(entity);
        Assert.Equal(
            new[] { nameof(PersistedHistoricalDataset.VersionIdentity) },
            entity!.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(64, entity.FindProperty(nameof(PersistedHistoricalDataset.ContentFingerprint))!.GetMaxLength());
        Assert.DoesNotContain(entity.GetProperties(), property =>
            property.Name.Contains("Owner", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(typeof(HistoricalDataset).Assembly.GetReferencedAssemblies()
            .Where(reference => reference.Name!.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase) ||
                reference.Name.Contains("Kraken", StringComparison.OrdinalIgnoreCase)));
    }

    private static TradingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HistoricalDataset CreateDataset(
        string id = "request-1",
        string source = "public-market-archive",
        string symbol = "BTC/USD",
        string interval = "1h",
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int candleCount = 1,
        string contentFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string sourceVersion = "archive-v1",
        DateTimeOffset? createdAtUtc = null)
    {
        var from = fromUtc ?? At(0);
        var to = toUtc ?? from;
        return new HistoricalDataset(
            id, source, symbol, interval, from, to, candleCount, contentFingerprint, sourceVersion, createdAtUtc ?? At(2));
    }

    private static DateTimeOffset At(int hour) => new(2026, 9, 20, hour, 0, 0, TimeSpan.Zero);
}
