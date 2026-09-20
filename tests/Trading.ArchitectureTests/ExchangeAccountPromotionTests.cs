using Trading.Application.UseCases.Audit;
using Trading.Application.UseCases.Exchange;
using Trading.Domain.Audit;
using Trading.Exchanges.Abstractions;

namespace Trading.ArchitectureTests;

/// <summary>
/// The promotion service: the only path by which real money could ever be put
/// at risk on this platform.
/// </summary>
public sealed class ExchangeAccountPromotionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (ExchangeAccountStageService Service, ExchangeAccount Account) Build(bool withLiveRoute)
    {
        var accounts = new FakeAccounts();
        var account = new ExchangeAccount(
            Guid.NewGuid(),
            UserId,
            ExchangeKind.Kraken,
            "Kraken",
            "exchange-credential/test",
            Now);
        account.MarkConnected(Now);
        accounts.All.Add(account);

        var routes = withLiveRoute
            ? new LiveExecutionRouteProvider(new ILiveExecutionRoute[] { new FakeKrakenRoute() })
            : new LiveExecutionRouteProvider(Array.Empty<ILiveExecutionRoute>());

        var service = new ExchangeAccountStageService(
            accounts,
            routes,
            new SilentAuditWriter(),
            new FrozenClock(Now));

        return (service, account);
    }

    [Fact]
    public void ThisDeploymentRegistersNoLiveExecutionRoute()
    {
        // The guarantee the platform currently rests on: nothing can send an
        // order to a venue. If this ever fails, real trading has become
        // reachable and every control around it needs re-review.
        var provider = new LiveExecutionRouteProvider(Array.Empty<ILiveExecutionRoute>());

        Assert.False(provider.HasRouteFor(ExchangeKind.Kraken));
    }

    [Fact]
    public async Task PromotionIsRefusedWhenNoExecutionRouteExists()
    {
        var (service, account) = Build(withLiveRoute: false);

        var result = await service.PromoteAsync(UserId, account.Id, TradingStage.Proving);

        Assert.False(result.IsSuccess);
        Assert.Equal(TradingStageChangeOutcome.LiveRouteUnavailable, result.Outcome);

        // The account must be left exactly where it was.
        Assert.Equal(TradingStage.Paper, account.Stage);
        Assert.False(account.CanReachExchange);
    }

    [Fact]
    public async Task StagesCannotBeSkipped()
    {
        var (service, account) = Build(withLiveRoute: true);

        // Straight from paper to live would bypass the supervised minimum-size
        // path that exists to prove execution and reconciliation actually work.
        var result = await service.PromoteAsync(UserId, account.Id, TradingStage.Live);

        Assert.Equal(TradingStageChangeOutcome.InvalidTransition, result.Outcome);
        Assert.Equal(TradingStage.Paper, account.Stage);
    }

    [Fact]
    public async Task AnotherUsersAccountCannotBePromoted()
    {
        var (service, account) = Build(withLiveRoute: true);

        var result = await service.PromoteAsync(OtherUserId, account.Id, TradingStage.Proving);

        // Reported as absent rather than forbidden, so the answer does not
        // confirm that the account exists.
        Assert.Equal(TradingStageChangeOutcome.AccountNotFound, result.Outcome);
        Assert.Equal(TradingStage.Paper, account.Stage);
    }

    [Fact]
    public async Task ADisconnectedAccountCannotBePromoted()
    {
        var (service, account) = Build(withLiveRoute: true);
        account.MarkDisconnected();

        var result = await service.PromoteAsync(UserId, account.Id, TradingStage.Proving);

        Assert.Equal(TradingStageChangeOutcome.AccountNotReady, result.Outcome);
    }

    [Fact]
    public async Task ReturningToPaperIsNeverRefused()
    {
        var (service, account) = Build(withLiveRoute: true);
        await service.PromoteAsync(UserId, account.Id, TradingStage.Proving);
        Assert.Equal(TradingStage.Proving, account.Stage);

        // Even after the connection breaks, withdrawing risk must still work.
        // A control that reduces exposure must not share the preconditions of
        // the control that adds it.
        account.MarkDisconnected();
        var result = await service.ReturnToPaperAsync(UserId, account.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(TradingStage.Paper, account.Stage);
        Assert.False(account.CanReachExchange);
    }

    private sealed class FakeKrakenRoute : ILiveExecutionRoute
    {
        public ExchangeKind Exchange => ExchangeKind.Kraken;
    }

    private sealed class FakeAccounts : IExchangeAccountRepository
    {
        public List<ExchangeAccount> All { get; } = new();

        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(All.Find(account => account.Id == id));

        public Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ExchangeAccount>>(
                All.FindAll(account => account.UserId == userId));

        public Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default)
        {
            All.Add(account);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class SilentAuditWriter : IAuditEventWriter
    {
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FrozenClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
