extern alias WebApp;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trading.Application.Execution;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Audit;
using Trading.Infrastructure.Data.Execution;
using LiveReconciliationWorker = WebApp::Trading.Web.LiveReconciliationWorker;

namespace Trading.ArchitectureTests;

public sealed class LiveOrderFillAccountingTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private const string Symbol = "XBTUSD";

    [Fact]
    public async Task ObservedFillsGrowPositionAtActualWeightedCostAndReduceWithoutInventingShort()
    {
        var harness = new Harness();
        var first = await harness.AddOrderAsync("first", OrderSide.Buy, 1m);
        harness.Gateway.Answer("first", 1m, ExchangeOrderState.Filled,
            Fill("first-fill", "first", SpotOrderSide.Buy, 1m, 90m));

        Assert.Equal(1, await harness.Sync.SyncAsync(Owner));
        var opened = Assert.Single(await harness.Positions.ListOpenAsync(Owner, CancellationToken.None));
        Assert.Equal(90m, opened.EntryPrice);
        Assert.Equal(1m, opened.Quantity);
        Assert.Equal(OrderState.Filled, first.State);

        var second = await harness.AddOrderAsync("second", OrderSide.Buy, 2m);
        var firstPart = Fill("second-1", "second", SpotOrderSide.Buy, 1m, 100m);
        var secondPart = Fill("second-2", "second", SpotOrderSide.Buy, 1m, 110m);
        harness.Gateway.Answer("second", 1m, ExchangeOrderState.PartiallyFilled, firstPart);

        Assert.Equal(1, await harness.Sync.SyncAsync(Owner));
        Assert.Equal(2m, opened.Quantity);
        Assert.Equal(95m, opened.EntryPrice);
        Assert.Equal(OrderState.PartiallyFilled, second.State);

        harness.Gateway.Answer("second", 2m, ExchangeOrderState.Filled, firstPart, secondPart);
        Assert.Equal(1, await harness.Sync.SyncAsync(Owner));
        Assert.Equal(3m, opened.Quantity);
        Assert.Equal(100m, opened.EntryPrice);
        Assert.Equal(0, await harness.Sync.SyncAsync(Owner));

        await harness.AddOrderAsync("sell", OrderSide.Sell, 1m);
        harness.Gateway.Answer("sell", 1m, ExchangeOrderState.Filled,
            Fill("sell-fill", "sell", SpotOrderSide.Sell, 1m, 115m));
        Assert.Equal(1, await harness.Sync.SyncAsync(Owner));
        Assert.Equal(2m, opened.Quantity);
        Assert.Equal(100m, opened.EntryPrice);
        Assert.Equal(115m, opened.MarkPrice);
        Assert.Equal(30m, opened.UnrealizedPnl);
    }

    [Fact]
    public async Task MissingOrConflictingFillEvidenceCannotBecomeAClaimedPosition()
    {
        foreach (var unsupported in new[]
        {
            SpotFillQueryResult.Unavailable("no response"),
            SpotFillQueryResult.Answered([]),
            SpotFillQueryResult.Answered([Fill("bad", "other", SpotOrderSide.Buy, 1m, 100m)]),
            SpotFillQueryResult.Answered([Fill("bad", "buy", SpotOrderSide.Sell, 1m, 100m)]),
            SpotFillQueryResult.Answered([Fill("bad", "buy", SpotOrderSide.Buy, 1m, 0m)])
        })
        {
            var harness = new Harness();
            var order = await harness.AddOrderAsync("buy", OrderSide.Buy, 1m);
            harness.Gateway.Answer("buy", 1m, ExchangeOrderState.Filled, unsupported);

            await Assert.ThrowsAsync<InvalidDataException>(() => harness.Sync.SyncAsync(Owner));
            Assert.Equal(0m, order.FilledQuantity);
            Assert.Empty(await harness.Positions.ListOpenAsync(Owner, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ASpotSellWithoutAnOwnedPositionCannotCreateASyntheticShort()
    {
        var harness = new Harness();
        var order = await harness.AddOrderAsync("sell", OrderSide.Sell, 1m);
        harness.Gateway.Answer("sell", 1m, ExchangeOrderState.Filled,
            Fill("sell-fill", "sell", SpotOrderSide.Sell, 1m, 99m));

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Sync.SyncAsync(Owner));
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Empty(await harness.Positions.ListOpenAsync(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task SamePairOnTwoOwnedAccountsNeverMergesTheirLivePositions()
    {
        var harness = new Harness();
        await harness.AddOrderAsync("first-account", OrderSide.Buy, 1m);
        harness.Gateway.Answer("first-account", 1m, ExchangeOrderState.Filled,
            Fill("first-fill", "first-account", SpotOrderSide.Buy, 1m, 90m));
        await harness.Sync.SyncAsync(Owner);

        await harness.AddOrderAsync("second-account", OrderSide.Buy, 1m, harness.OtherAccountId);
        harness.Gateway.Answer("second-account", 1m, ExchangeOrderState.Filled,
            Fill("second-fill", "second-account", SpotOrderSide.Buy, 1m, 110m));
        await harness.Sync.SyncAsync(Owner);

        var positions = await harness.Positions.ListOpenAsync(Owner, CancellationToken.None);
        Assert.Equal(2, positions.Count);
        Assert.Equal(90m, Assert.Single(positions, p => p.ExchangeAccountId == harness.AccountId).EntryPrice);
        Assert.Equal(110m, Assert.Single(positions, p => p.ExchangeAccountId == harness.OtherAccountId).EntryPrice);
    }

    [Fact]
    public async Task AnUnboundLegacyLivePositionBlocksAmbiguousFillAtTheSamePair()
    {
        var harness = new Harness();
        await harness.Positions.AddAsync(new Position(
            Guid.NewGuid(), Owner, Guid.NewGuid(), Symbol, PositionDirection.DirectionLong,
            1m, 90m, 90m, Now.AddMinutes(-6), mode: TradingMode.Live), CancellationToken.None);
        var order = await harness.AddOrderAsync("bound-order", OrderSide.Buy, 1m);
        harness.Gateway.Answer("bound-order", 1m, ExchangeOrderState.Filled,
            Fill("bound-fill", "bound-order", SpotOrderSide.Buy, 1m, 100m));

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Sync.SyncAsync(Owner));
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Single(await harness.Positions.ListOpenAsync(Owner, CancellationToken.None));
    }

    [Theory]
    [InlineData(ExchangeOrderState.Filled, "1")]
    [InlineData(ExchangeOrderState.PartiallyFilled, "0.5")]
    public async Task UnknownLiveOrderOnlyResolvesAfterFillAndPositionCommit(
        ExchangeOrderState state, string observedQuantity)
    {
        var harness = new Harness();
        var order = await harness.AddOrderAsync("unknown", OrderSide.Buy, 1m);
        var record = await harness.Reconciliation.OpenAsync(
            order, "spot-adapter", "No submission response.", CancellationToken.None);
        var quantity = decimal.Parse(observedQuantity, System.Globalization.CultureInfo.InvariantCulture);
        harness.Gateway.Answer("unknown", quantity, state,
            Fill("unknown-fill", "unknown", SpotOrderSide.Buy, quantity, 92m));

        var premature = await harness.Reconciliation.ResolveAsync(record, CancellationToken.None);
        Assert.Equal(ReconciliationDisposition.Unresolved, premature.Disposition);
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Empty(await harness.Positions.ListOpenAsync(Owner, CancellationToken.None));
        Assert.False(record.IsResolved);

        Assert.Equal(1, await harness.Sync.SyncAsync(Owner));
        Assert.Equal(quantity, order.FilledQuantity);
        var position = Assert.Single(await harness.Positions.ListOpenAsync(Owner, CancellationToken.None));
        Assert.Equal(quantity, position.Quantity);
        Assert.Equal(92m, position.EntryPrice);
        Assert.True(order.RequiresReconciliation);
        Assert.False(record.IsResolved);

        var resolved = await harness.Reconciliation.ResolveAsync(record, CancellationToken.None);
        Assert.Equal(ReconciliationDisposition.AdoptedExchangeState, resolved.Disposition);
        Assert.False(order.RequiresReconciliation);
        Assert.True(record.IsResolved);
        Assert.Equal(0, await harness.Sync.SyncAsync(Owner));
    }

    [Fact]
    public async Task PeriodicReadOnlyPassAccountsForUnknownLiveFillBeforeResolving()
    {
        var harness = new Harness();
        var order = await harness.AddOrderAsync("periodic", OrderSide.Buy, 1m);
        var record = await harness.Reconciliation.OpenAsync(
            order, "spot-adapter", "No submission response.", CancellationToken.None);
        harness.Gateway.Answer("periodic", 1m, ExchangeOrderState.Filled,
            Fill("periodic-fill", "periodic", SpotOrderSide.Buy, 1m, 93m));

        using var services = new ServiceCollection()
            .AddSingleton<IOrderRepository>(harness.Orders)
            .AddSingleton<IOrderReconciliationRepository>(harness.Records)
            .AddScoped(_ => harness.Sync)
            .AddScoped(_ => harness.Reconciliation)
            .BuildServiceProvider();
        using var worker = new LiveReconciliationWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTime(), NullLogger<LiveReconciliationWorker>.Instance);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.True(record.IsResolved);
        Assert.False(order.RequiresReconciliation);
        Assert.Equal(OrderState.Filled, order.State);
        Assert.Equal(93m, Assert.Single(await harness.Positions.ListOpenAsync(
            Owner, CancellationToken.None)).EntryPrice);
    }

    [Fact]
    public async Task PeriodicPassLeavesUnknownLiveOrderFrozenWithoutCompleteFills()
    {
        var harness = new Harness();
        var order = await harness.AddOrderAsync("unproven", OrderSide.Buy, 1m);
        var record = await harness.Reconciliation.OpenAsync(
            order, "spot-adapter", "No submission response.", CancellationToken.None);
        harness.Gateway.Answer("unproven", 1m, ExchangeOrderState.Filled,
            SpotFillQueryResult.Unavailable("Missing trade evidence."));

        using var services = new ServiceCollection()
            .AddSingleton<IOrderRepository>(harness.Orders)
            .AddSingleton<IOrderReconciliationRepository>(harness.Records)
            .AddScoped(_ => harness.Sync)
            .AddScoped(_ => harness.Reconciliation)
            .BuildServiceProvider();
        using var worker = new LiveReconciliationWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedTime(), NullLogger<LiveReconciliationWorker>.Instance);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.False(record.IsResolved);
        Assert.True(order.RequiresReconciliation);
        Assert.Equal(0m, order.FilledQuantity);
        Assert.Empty(await harness.Positions.ListOpenAsync(Owner, CancellationToken.None));
    }

    [Fact]
    public async Task SqlPositionOrderAndAuditCommitOrRollBackTogether()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingLiveFill_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var owner = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var positionId = Guid.NewGuid();

        try
        {
            await using (var db = Context())
            {
                await db.Database.MigrateAsync();
                var order = new Order(orderId, owner, Guid.NewGuid(), Symbol,
                    OrderSide.Buy, OrderType.Limit, 2m, 120m, Now.AddMinutes(-5),
                    $"live-{Guid.NewGuid():N}", mode: TradingMode.Live,
                    exchangeAccountId: Guid.NewGuid());
                order.MarkSubmitted(null, Now.AddMinutes(-4));
                db.Orders.Add(order);
                db.Positions.Add(new Position(positionId, owner, order.StrategyId, Symbol,
                    PositionDirection.DirectionLong, 1m, 90m, 90m, Now.AddMinutes(-6),
                    mode: TradingMode.Live));
                await db.SaveChangesAsync();
            }

            async Task ApplyAsync(TradingDbContext db)
            {
                var positions = new EfPositionRepository(db);
                var orders = new EfOrderRepository(db);
                var position = (await positions.GetAsync(owner, positionId, CancellationToken.None))!;
                var order = (await orders.GetAsync(owner, orderId, CancellationToken.None))!;
                var expectedPositionVersion = position.Version;
                var expectedOrderVersion = order.Version;
                position.Increase(1m, 110m);
                await positions.UpdateAsync(position, expectedPositionVersion, CancellationToken.None);
                order.MarkPartiallyFilled(1m, Now);
                await orders.UpdateAsync(order, expectedOrderVersion, CancellationToken.None);
                await new EfAuditEventWriter(db).WriteAsync(
                    new AuditEvent(Guid.NewGuid(), owner, "LiveOrderSynced", "LiveOrder",
                        orderId.ToString("D"), Now, null, "Observed fill", order.ClientOrderId));
            }

            await using (var db = Context())
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    new EfLiveFillPersistenceTransaction(db).RunAsync(async _ =>
                    {
                        await ApplyAsync(db);
                        throw new InvalidOperationException("Fail after all three writes.");
                    }, CancellationToken.None));

            Position stalePosition;
            Order staleOrder;
            await using (var fresh = Context())
            {
                stalePosition = (await new EfPositionRepository(fresh)
                    .GetAsync(owner, positionId, CancellationToken.None))!;
                staleOrder = (await new EfOrderRepository(fresh)
                    .GetAsync(owner, orderId, CancellationToken.None))!;
                Assert.Equal(1m, stalePosition.Quantity);
                Assert.Equal(0m, staleOrder.FilledQuantity);
                Assert.Empty(await fresh.AuditEvents.ToListAsync());
            }

            await using (var db = Context())
                await new EfLiveFillPersistenceTransaction(db).RunAsync(
                    _ => ApplyAsync(db), CancellationToken.None);

            await using (var fresh = Context())
            {
                Assert.Equal(2m, (await fresh.Positions.SingleAsync(p => p.Id == positionId)).Quantity);
                Assert.Equal(100m, (await fresh.Positions.SingleAsync(p => p.Id == positionId)).EntryPrice);
                Assert.Equal(1m, (await fresh.Orders.SingleAsync(o => o.Id == orderId)).FilledQuantity);
                Assert.Single(await fresh.AuditEvents.ToListAsync());
            }

            var stalePositionVersion = stalePosition.Version;
            stalePosition.Increase(1m, 120m);
            await using (var fresh = Context())
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                    new EfPositionRepository(fresh).UpdateAsync(
                        stalePosition, stalePositionVersion, CancellationToken.None));

            var staleOrderVersion = staleOrder.Version;
            staleOrder.MarkPartiallyFilled(1m, Now);
            await using (var fresh = Context())
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                    new EfOrderRepository(fresh).UpdateAsync(
                        staleOrder, staleOrderVersion, CancellationToken.None));
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SqlAcceptsAtMostOneUnresolvedLiveOrderPerAccountAcrossContexts()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingLiveAdmission_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        Order NewOrder(Guid accountId) => new(Guid.NewGuid(), owner, Guid.NewGuid(), Symbol,
            OrderSide.Buy, OrderType.Limit, 1m, 100m, Now,
            Guid.NewGuid().ToString("D"), mode: TradingMode.Live, exchangeAccountId: accountId);

        try
        {
            await using (var db = Context())
                await db.Database.MigrateAsync();

            await using var firstContext = Context();
            await using var secondContext = Context();
            var outcomes = await Task.WhenAll(
                TryAddAsync(new EfOrderRepository(firstContext), NewOrder(account)),
                TryAddAsync(new EfOrderRepository(secondContext), NewOrder(account)));
            Assert.Equal(1, outcomes.Count(accepted => accepted));

            await using (var db = Context())
            {
                var recorded = Assert.Single(await db.Orders.ToListAsync());
                Assert.Equal(account, recorded.ExchangeAccountId);
                recorded.MarkRejected("No exchange submission occurred.");
                await new EfOrderRepository(db).UpdateAsync(recorded, CancellationToken.None);
            }
            await using (var db = Context())
                await new EfOrderRepository(db).AddAsync(NewOrder(account), CancellationToken.None);
            await using (var db = Context())
            {
                var unresolved = await db.Orders.SingleAsync(order =>
                    order.ExchangeAccountId == account && order.State == OrderState.Draft);
                unresolved.MarkUnknown("Unconfirmed exchange status.", Now.AddMinutes(1));
                unresolved.MarkRejected("Recorded rejection without reconciling the unknown status.");
                await new EfOrderRepository(db).UpdateAsync(unresolved, CancellationToken.None);
            }
            await using (var db = Context())
                await Assert.ThrowsAsync<WorkingLiveOrderConflictException>(() =>
                    new EfOrderRepository(db).AddAsync(NewOrder(account), CancellationToken.None));
            await using (var db = Context())
                await new EfOrderRepository(db).AddAsync(NewOrder(Guid.NewGuid()), CancellationToken.None);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.Database.EnsureDeletedAsync();
        }

        static async Task<bool> TryAddAsync(EfOrderRepository repository, Order order)
        {
            try
            {
                await repository.AddAsync(order, CancellationToken.None);
                return true;
            }
            catch (WorkingLiveOrderConflictException)
            {
                return false;
            }
        }
    }

        [Fact]
        public async Task SqlUnknownOrderFreezeAndResolutionAreAtomicWithAudit()
        {
            var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
            if (string.IsNullOrWhiteSpace(server))
                return;
            var connection = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = $"TradingLiveReconciliation_{Guid.NewGuid():N}",
                IntegratedSecurity = true,
                Encrypt = false,
                TrustServerCertificate = true
            }.ConnectionString;
            TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            var owner = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var status = new FixedStatusQuery(OrderStatusQueryResult.Found(ExchangeOrderState.New, "EX-1", 0m));

            static OrderReconciliationService Service(
                TradingDbContext db, IExchangeOrderStatusQuery statusQuery, bool failAudit) =>
                new(new EfOrderRepository(db), new EfOrderReconciliationRepository(db),
                    statusQuery, new OptionallyFailingAuditWriter(db, failAudit),
                    new FixedTime(), new EfLiveFillPersistenceTransaction(db));

            try
            {
                await using (var db = Context())
                {
                    await db.Database.MigrateAsync();
                    await new EfOrderRepository(db).AddAsync(new Order(
                        orderId, owner, Guid.NewGuid(), Symbol, OrderSide.Buy,
                        OrderType.Limit, 1m, 100m, Now.AddMinutes(-5),
                        Guid.NewGuid().ToString("D"), mode: TradingMode.Live,
                        exchangeAccountId: Guid.NewGuid()), CancellationToken.None);
                }

                await using (var db = Context())
                {
                    var order = (await new EfOrderRepository(db)
                        .GetAsync(owner, orderId, CancellationToken.None))!;
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        Service(db, status, failAudit: true).OpenAsync(
                            order, "live", "Unknown response.", CancellationToken.None));
                }
                await using (var db = Context())
                {
                    Assert.False((await db.Orders.SingleAsync()).RequiresReconciliation);
                    Assert.Empty(await db.OrderReconciliations.ToListAsync());
                    Assert.Empty(await db.AuditEvents.ToListAsync());
                }

                Guid recordId;
                await using (var db = Context())
                {
                    var order = (await new EfOrderRepository(db)
                        .GetAsync(owner, orderId, CancellationToken.None))!;
                    var record = await Service(db, status, failAudit: false).OpenAsync(
                        order, "live", "Unknown response.", CancellationToken.None);
                    recordId = record.Id;
                }

                await using (var db = Context())
                {
                    var record = (await new EfOrderReconciliationRepository(db)
                        .GetAsync(recordId, CancellationToken.None))!;
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        Service(db, status, failAudit: true).ResolveAsync(record, CancellationToken.None));
                }
                await using (var db = Context())
                {
                    Assert.True((await db.Orders.SingleAsync()).RequiresReconciliation);
                    Assert.Null((await db.OrderReconciliations.SingleAsync()).ResolvedAtUtc);
                    Assert.Single(await db.AuditEvents.ToListAsync());
                }

                await using (var db = Context())
                {
                    var record = (await new EfOrderReconciliationRepository(db)
                        .GetAsync(recordId, CancellationToken.None))!;
                    var result = await Service(db, status, failAudit: false)
                        .ResolveAsync(record, CancellationToken.None);
                    Assert.Equal(ReconciliationDisposition.AdoptedExchangeState, result.Disposition);
                }
                await using (var db = Context())
                {
                    Assert.False((await db.Orders.SingleAsync()).RequiresReconciliation);
                    Assert.NotNull((await db.OrderReconciliations.SingleAsync()).ResolvedAtUtc);
                    Assert.Equal(2, await db.AuditEvents.CountAsync());
                }
            }
            finally
            {
                await using var cleanup = Context();
                await cleanup.Database.EnsureDeletedAsync();
            }
        }

    private static SpotFill Fill(string id, string exchangeOrderId, SpotOrderSide side,
        decimal quantity, decimal price) =>
        new(id, exchangeOrderId, Symbol, side, quantity, price, 0m, "USD", Now.AddMinutes(-1));

    private sealed class Harness
    {
        private readonly ExchangeAccount _account = new(
            Guid.NewGuid(), Owner, ExchangeKind.Kraken, "replay",
            "secret-reference", Now.AddMinutes(-10));
        private readonly ExchangeAccount _otherAccount = new(
            Guid.NewGuid(), Owner, ExchangeKind.Kraken, "second-replay",
            "other-secret-reference", Now.AddMinutes(-10));
        private readonly InMemoryOrderRepository _orders = new();

        public Harness()
        {
            Gateway = new ReplayGateway();
            Positions = new InMemoryPositionRepository();
            Records = new InMemoryOrderReconciliationRepository();
            var accounts = new AccountSource(_account, _otherAccount);
            var credentials = new CredentialSource(_account.Id, _otherAccount.Id);
            var time = new FixedTime();
            var audit = new InMemoryAuditEventWriter();
            Sync = new LiveOrderSyncService(
                _orders, Positions, accounts, Gateway, credentials, audit,
                new PassThroughPersistence(), time);
            Reconciliation = new OrderReconciliationService(
                _orders, Records, new OrderBackedSpotOrderStatusQuery(
                    Gateway, _orders, accounts, credentials), audit, time,
                new PassThroughLiveFillTransaction());
        }

        public Guid AccountId => _account.Id;
        public Guid OtherAccountId => _otherAccount.Id;
        public IOrderRepository Orders => _orders;
        public ReplayGateway Gateway { get; }
        public InMemoryPositionRepository Positions { get; }
        public LiveOrderSyncService Sync { get; }
        public InMemoryOrderReconciliationRepository Records { get; }
        public OrderReconciliationService Reconciliation { get; }

        public async Task<Order> AddOrderAsync(string id, OrderSide side, decimal quantity, Guid? accountId = null)
        {
            var order = new Order(Guid.NewGuid(), Owner, Guid.NewGuid(), Symbol, side,
                OrderType.Limit, quantity, 120m, Now.AddMinutes(-5), id,
                mode: TradingMode.Live, exchangeAccountId: accountId ?? _account.Id);
            order.MarkSubmitted(null, Now.AddMinutes(-4));
            await _orders.AddAsync(order, CancellationToken.None);
            return order;
        }
    }

    private sealed class ReplayGateway : ISpotOrderGateway
    {
        private readonly Dictionary<string, (OrderStatusQueryResult Status, SpotFillQueryResult Fills)> _answers = [];
        private string? _queriedOrderId;
        public ExchangeKind Exchange => ExchangeKind.Kraken;

        public void Answer(string id, decimal quantity, ExchangeOrderState state, params SpotFill[] fills) =>
            Answer(id, quantity, state, SpotFillQueryResult.Answered(fills));

        public void Answer(string id, decimal quantity, ExchangeOrderState state, SpotFillQueryResult fills) =>
            _answers[id] = (OrderStatusQueryResult.Found(state, id, quantity), fills);

        public Task<OrderStatusQueryResult> QueryAsync(ExchangeCredential credential, string clientOrderId,
            CancellationToken cancellationToken = default)
        {
            _queriedOrderId = clientOrderId;
            return Task.FromResult(_answers[clientOrderId].Status);
        }

        public Task<SpotFillQueryResult> ListFillsAsync(ExchangeCredential credential,
            DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(_answers[_queriedOrderId ?? throw new InvalidOperationException()].Fills);

        public Task<SpotOrderPlacement> PlaceAsync(ExchangeCredential credential, SpotOrderRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpotOrderCancellation> CancelAsync(ExchangeCredential credential, string clientOrderId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AccountSource(ExchangeAccount account, ExchangeAccount other) : IExchangeAccountRepository
    {
        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(id == account.Id ? account : id == other.Id ? other : null);
        public Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ExchangeAccount>>(userId == Owner ? [account, other] : []);
        public Task AddAsync(ExchangeAccount value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(ExchangeAccount value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CredentialSource(Guid accountId, Guid otherAccountId) : ISpotExecutionAccountSource
    {
        public Task<SpotExecutionAccount?> ResolveAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<SpotExecutionAccount?>(id == accountId || id == otherAccountId
                ? new(id, Owner, ExchangeKind.Kraken, TradingStage.Live,
                    new ExchangeCredential("replay", "replay-secret"))
                : null);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixedStatusQuery(OrderStatusQueryResult status) : IExchangeOrderStatusQuery
    {
        public Task<OrderStatusQueryResult> QueryByClientOrderIdAsync(
            string clientOrderId, string symbol, CancellationToken cancellationToken) =>
            Task.FromResult(status);
    }

    private sealed class OptionallyFailingAuditWriter(TradingDbContext db, bool fail) : IAuditEventWriter
    {
        public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            await new EfAuditEventWriter(db).WriteAsync(auditEvent, cancellationToken);
            if (fail)
                throw new InvalidOperationException("Simulated audit persistence failure.");
        }
    }

    private sealed class PassThroughPersistence : ILiveFillPersistenceTransaction
    {
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);
    }
}
