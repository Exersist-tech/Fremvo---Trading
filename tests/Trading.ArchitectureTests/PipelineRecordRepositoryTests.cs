using Trading.Application.Pipeline;
using Trading.Domain.Execution;
using Trading.Domain.Market;

namespace Trading.ArchitectureTests;

public sealed class PipelineRecordRepositoryTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();

    private static MarketEvent Event(string symbol = "BTCUSDT") =>
        new(Guid.NewGuid(), symbol, CandleInterval.OneMinute, DateTimeOffset.UtcNow, 100m, 5m, isClosed: true);

    private static PipelineRecord<MarketEvent> Record(Guid userId, string correlationId, MarketEvent payload) =>
        new(
            Guid.NewGuid(),
            new PipelineContext(userId, TradingMode.Paper, correlationId),
            PipelineStage.MarketEvent,
            payload,
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task ListForUserNeverReturnsAnotherUsersRecords()
    {
        var repository = new InMemoryMarketEventRepository();
        await repository.AddAsync(Record(UserA, "corr-a", Event()));
        await repository.AddAsync(Record(UserA, "corr-a", Event()));
        await repository.AddAsync(Record(UserB, "corr-b", Event()));

        var forA = await repository.ListForUserAsync(UserA);
        var forB = await repository.ListForUserAsync(UserB);

        Assert.Equal(2, forA.Count);
        Assert.Single(forB);
        Assert.All(forA, r => Assert.Equal(UserA, r.Context.UserId));
        Assert.All(forB, r => Assert.Equal(UserB, r.Context.UserId));
    }

    [Fact]
    public async Task GetReturnsNullWhenRecordBelongsToAnotherUser()
    {
        var repository = new InMemoryMarketEventRepository();
        var owned = Record(UserA, "corr-a", Event());
        await repository.AddAsync(owned);

        Assert.NotNull(await repository.GetAsync(UserA, owned.Id));
        Assert.Null(await repository.GetAsync(UserB, owned.Id));
    }

    [Fact]
    public async Task ListByCorrelationIsScopedToTheOwningUser()
    {
        var repository = new InMemoryMarketEventRepository();
        await repository.AddAsync(Record(UserA, "shared-correlation", Event()));
        await repository.AddAsync(Record(UserB, "shared-correlation", Event()));

        var forA = await repository.ListByCorrelationAsync(UserA, "shared-correlation");

        Assert.Single(forA);
        Assert.Equal(UserA, forA.Single().Context.UserId);
    }

    [Fact]
    public async Task DuplicateRecordIdIsRejected()
    {
        var repository = new InMemoryMarketEventRepository();
        var record = Record(UserA, "corr-a", Event());
        await repository.AddAsync(record);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(record));
    }

    [Fact]
    public void PipelineContextRequiresOwningUserAndCorrelation()
    {
        Assert.Throws<ArgumentException>(() =>
            new PipelineContext(Guid.Empty, TradingMode.Paper, "corr"));

        Assert.Throws<ArgumentException>(() =>
            new PipelineContext(UserA, TradingMode.Paper, "   "));
    }

    [Fact]
    public void PipelineContextDistinguishesPaperFromLive()
    {
        Assert.True(new PipelineContext(UserA, TradingMode.Paper, "corr").IsPaper);
        Assert.False(new PipelineContext(UserA, TradingMode.Live, "corr").IsPaper);
    }
}
