using Microsoft.EntityFrameworkCore;
using Trading.Application.Scanner;
using Trading.Domain.Market;
using Trading.Domain.Scanner;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Scanner;

namespace Trading.ArchitectureTests;

public sealed class ScannerPersistenceTests
{
    private static readonly string[] s_expectedNormalizedSymbols = ["BTC/USD", "ETH/USD"];
    private static readonly ScanCriterionKind[] s_volumeCriterion =
        [ScanCriterionKind.MinimumCandleVolume];
    private static readonly string[] s_defaultSymbols = ["BTC/USD", "ETH/USD", "SOL/USD"];
    private static readonly ScanCriterion[] s_defaultCriteria =
        [new ScanCriterion(ScanCriterionKind.MinimumCandleVolume, 1000m)];

    [Fact]
    public void DomainModelsValidateBoundedImmutableAndUtcConfiguration()
    {
        var ownerId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(2));
        var symbols = new[] { "eth/usd", "BTC/USD", "BTC/USD" };
        var criteria = new[]
        {
            new ScanCriterion(ScanCriterionKind.MinimumCandleVolume, 1000.123456789012m),
            new ScanCriterion(ScanCriterionKind.MinimumClosedCandleCount, 50m)
        };

        var request = new ScanRequest(
            Guid.NewGuid(),
            ownerId,
            "Momentum screen",
            symbols,
            CandleInterval.OneHour,
            criteria,
            25,
            createdAt);

        Assert.Equal(s_expectedNormalizedSymbols, request.Symbols);
        Assert.Equal(createdAt.ToUniversalTime(), request.CreatedAtUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanCriterion(ScanCriterionKind.MinimumClosedCandleCount, 1.5m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanRequest(
                Guid.NewGuid(), ownerId, "invalid", Array.Empty<string>(), CandleInterval.OneHour,
                criteria, 1, createdAt));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanResult(
                ownerId, Guid.NewGuid(), Guid.NewGuid(), "BTC/USD", 0, 0.5m,
                new List<ScanCriterionKind> { ScanCriterionKind.MinimumCandleVolume }, createdAt, createdAt));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanResult(
                ownerId, Guid.NewGuid(), Guid.NewGuid(), "BTC/USD", 1, 1.01m,
                new List<ScanCriterionKind> { ScanCriterionKind.MinimumCandleVolume }, createdAt, createdAt));
    }

    [Fact]
    public async Task RequestReadsAndWritesAreOwnerIsolatedAsync()
    {
        await using var context = CreateContext();
        var repository = new EfScanRequestRepository(context);
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var request = CreateRequest(owner, "Owned", At(10));

        await repository.AddAsync(owner, request);

        Assert.NotNull(await repository.GetAsync(owner, request.Id));
        Assert.Null(await repository.GetAsync(otherOwner, request.Id));
        Assert.Single(await repository.ListAsync(owner, 10));
        Assert.Empty(await repository.ListAsync(otherOwner, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AddAsync(otherOwner, request));
    }

    [Fact]
    public async Task ResultReadsAreOwnerIsolatedAndDeterministicallyRankedAsync()
    {
        await using var context = CreateContext();
        var requests = new EfScanRequestRepository(context);
        var results = new EfScanResultRepository(context);
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var request = CreateRequest(owner, "Owned", At(10));
        await requests.AddAsync(owner, request);
        var run = Guid.NewGuid();

        await results.RecordAsync(owner, CreateResult(owner, request.Id, run, "ETH/USD", 2, 0.6m));
        await results.RecordAsync(owner, CreateResult(owner, request.Id, run, "SOL/USD", 1, 0.5m));
        await results.RecordAsync(owner, CreateResult(owner, request.Id, run, "BTC/USD", 1, 0.5m));

        var ordered = await results.ListAsync(owner, request.Id, run, 2);

        Assert.Equal(new List<string> { "BTC/USD", "SOL/USD" }, ordered.Select(result => result.Symbol));
        Assert.Empty(await results.ListAsync(otherOwner, request.Id, run, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            results.RecordAsync(otherOwner, CreateResult(owner, request.Id, run, "XRP/USD", 3, 0.4m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            results.RecordAsync(owner, CreateResult(owner, request.Id, run, "XRP/USD", 3, 0.4m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            results.RecordAsync(owner, CreateResult(owner, request.Id, run, "BTC/USD", 26, 0.4m)));
    }

    [Fact]
    public async Task ExactDuplicateResultIsIdempotentAndConflictDoesNotOverwriteAsync()
    {
        await using var context = CreateContext();
        var requests = new EfScanRequestRepository(context);
        var results = new EfScanResultRepository(context);
        var owner = Guid.NewGuid();
        var request = CreateRequest(owner, "Owned", At(10));
        await requests.AddAsync(owner, request);
        var run = Guid.NewGuid();
        var result = CreateResult(owner, request.Id, run, "BTC/USD", 1, 0.9m);
        var conflicting = CreateResult(owner, request.Id, run, "BTC/USD", 2, 0.1m);

        Assert.Equal(ScanResultWriteResult.Inserted, await results.RecordAsync(owner, result));
        Assert.Equal(ScanResultWriteResult.Duplicate, await results.RecordAsync(owner, result));
        Assert.Equal(ScanResultWriteResult.Conflict, await results.RecordAsync(owner, conflicting));

        var stored = Assert.Single(await results.ListAsync(owner, request.Id, run, 10));
        Assert.Equal(1, stored.Rank);
        Assert.Equal(0.9m, stored.Score);
    }

    [Fact]
    public async Task RepositoryPropagatesCancellationAsync()
    {
        await using var context = CreateContext();
        var requests = new EfScanRequestRepository(context);
        var owner = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            requests.ListAsync(owner, 10, cancellation.Token));
    }

    [Fact]
    public void ScannerMappingsUseOwnerIndexesCompositeEvidenceIdentityAndDecimalScore()
    {
        using var context = CreateRelationalContext();
        var requestEntity = context.Model.FindEntityType(typeof(PersistedScanRequest));
        var resultEntity = context.Model.FindEntityType(typeof(PersistedScanResult));

        Assert.NotNull(requestEntity);
        Assert.NotNull(resultEntity);
        Assert.Equal("ScanRequests", requestEntity!.GetTableName());
        Assert.Equal("ScanResults", resultEntity!.GetTableName());
        Assert.Equal(
            new[] { nameof(PersistedScanResult.ScanRequestId), nameof(PersistedScanResult.ScanRunId), nameof(PersistedScanResult.Symbol) },
            resultEntity!.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal("decimal(18,12)", resultEntity.FindProperty(nameof(PersistedScanResult.Score))!.GetColumnType());
        Assert.Contains(
            resultEntity.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(new List<string>
            {
                nameof(PersistedScanResult.OwnerId),
                nameof(PersistedScanResult.ScanRequestId),
                nameof(PersistedScanResult.ScanRunId),
                nameof(PersistedScanResult.Rank),
                nameof(PersistedScanResult.Score),
                nameof(PersistedScanResult.Symbol)
            }));
    }

    [Fact]
    public void DomainAssemblyHasNoInfrastructureDependencies()
    {
        var references = typeof(ScanRequest).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name);

        Assert.DoesNotContain(references, name =>
            name?.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Contains("Azure", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Contains("Kraken", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Contains("Sql", StringComparison.OrdinalIgnoreCase) == true ||
            name?.Contains("Http", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static TradingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TradingDbContext CreateRelationalContext() =>
        new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=TradingScannerMappingTests;Trusted_Connection=True")
            .Options);

    private static ScanRequest CreateRequest(Guid ownerId, string name, DateTimeOffset createdAtUtc) =>
        new(
            Guid.NewGuid(),
            ownerId,
            name,
            s_defaultSymbols,
            CandleInterval.OneHour,
            s_defaultCriteria,
            25,
            createdAtUtc);

    private static ScanResult CreateResult(
        Guid ownerId,
        Guid requestId,
        Guid runId,
        string symbol,
        int rank,
        decimal score) =>
        new(
            ownerId,
            requestId,
            runId,
            symbol,
            rank,
            score,
            s_volumeCriterion,
            At(11),
            At(12));

    private static DateTimeOffset At(int hour) => new(2026, 9, 20, hour, 0, 0, TimeSpan.Zero);
}
