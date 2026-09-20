using Trading.Application.Execution;
using Trading.Domain.Execution;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;
using DomainPositionDirection = Trading.Domain.Execution.FuturesPositionDirection;

namespace Trading.ArchitectureTests;

public sealed class KrakenFuturesExecutionAdapterTests
{
    private static readonly Guid AccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidDemoLimitOrderMapsEveryFuturesFieldIncludingReduceOnly()
    {
        var gateway = new StubGateway(FuturesOrderPlacement.Create(FuturesPlacementOutcome.Accepted, "future-001"));
        var command = Command(reduceOnly: true);

        var result = await Adapter(gateway, DemoAccount()).ExecuteAsync(command);

        Assert.True(result.Success);
        Assert.Equal(ExecutionOutcome.Accepted, result.Outcome);
        Assert.Equal(1, gateway.PlaceCalls);
        Assert.NotNull(gateway.LastRequest);
        Assert.Equal(command.Symbol, gateway.LastRequest!.Symbol);
        Assert.Equal(FuturesOrderSide.Sell, gateway.LastRequest.Side);
        Assert.Equal(Trading.Exchanges.Abstractions.Execution.FuturesPositionDirection.LongPosition, gateway.LastRequest.PositionDirection);
        Assert.Equal(FuturesOrderType.Limit, gateway.LastRequest.Type);
        Assert.Equal(command.Quantity, gateway.LastRequest.Quantity);
        Assert.Equal(command.LimitPrice, gateway.LastRequest.LimitPrice);
        Assert.True(gateway.LastRequest.ReduceOnly);
        Assert.False(gateway.LastRequest.ValidateOnly);
    }

    [Fact]
    public async Task ReduceOnlyIsMappedExactlyWithoutInference()
    {
        var gateway = new StubGateway(FuturesOrderPlacement.Create(FuturesPlacementOutcome.Rejected, "future-001"));

        await Adapter(gateway, DemoAccount()).ExecuteAsync(Command(reduceOnly: false));

        Assert.False(gateway.LastRequest!.ReduceOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNonDemoAccountsNeverAttemptVenue(bool nonDemoAccount)
    {
        var gateway = new StubGateway(FuturesOrderPlacement.Create(FuturesPlacementOutcome.Accepted, "future-001"));
        var account = nonDemoAccount ? DemoAccount(isDemoOnly: false) : null;

        var result = await Adapter(gateway, account).ExecuteAsync(Command());

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal("Refused", result.Status);
        Assert.Equal(0, gateway.PlaceCalls);
    }

    [Fact]
    public async Task SpotAccountPathCannotBeUsedAsAFuturesRoute()
    {
        var gateway = new StubGateway(FuturesOrderPlacement.Create(FuturesPlacementOutcome.Accepted, "future-001"));
        var result = await Adapter(gateway, DemoAccount(exchange: ExchangeKind.None)).ExecuteAsync(Command());

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal(0, gateway.PlaceCalls);
    }

    [Fact]
    public async Task IndeterminateAndDuplicateOutcomesRequireReconciliationAndAreNotRetried()
    {
        var gateway = new StubGateway(FuturesOrderPlacement.Create(FuturesPlacementOutcome.Indeterminate, "future-001"));

        var result = await Adapter(gateway, DemoAccount()).ExecuteAsync(Command());

        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.True(result.RequiresReconciliation);
        Assert.Equal(1, gateway.PlaceCalls);
    }

    [Fact]
    public async Task DefiniteVenueRejectionDoesNotRequireReconciliation()
    {
        var gateway = new StubGateway(FuturesOrderPlacement.Create(FuturesPlacementOutcome.Rejected, "future-001"));

        var result = await Adapter(gateway, DemoAccount()).ExecuteAsync(Command());

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.False(result.RequiresReconciliation);
    }

    [Fact]
    public void CommandRequiresExplicitDemoAccountAndDefaultsToReduceOnly()
    {
        Assert.Throws<ArgumentException>(() => new FuturesExecutionCommand(
            Guid.NewGuid(), Guid.Empty, "PI_XBTUSD", TradeDirection.Sell,
            Trading.Domain.Execution.FuturesPositionDirection.LongPosition, 1m, 10m, Now, "future-001"));

        var command = Command();

        Assert.True(command.ReduceOnly);
    }

    [Fact]
    public void FuturesAdapterIsNotResolvableByWebOrLiveExecutionServices()
    {
        var forbiddenTypes = typeof(LiveTradingService).Assembly.GetTypes()
            .Where(type => type.Name.Contains("Live", StringComparison.Ordinal)
                           || type.Name.Contains("Web", StringComparison.Ordinal)
                           || type.Name.Contains("TradingService", StringComparison.Ordinal));

        Assert.DoesNotContain(
            forbiddenTypes.SelectMany(type => type.GetConstructors())
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType == typeof(KrakenFuturesExecutionAdapter)
                         || parameter.ParameterType == typeof(IFuturesExecutionAdapter)
                         || parameter.ParameterType == typeof(IFuturesOrderGateway));
    }

    private static FuturesExecutionCommand Command(bool reduceOnly = true) =>
        new(
            Guid.NewGuid(), AccountId, "PI_XBTUSD", TradeDirection.Sell,
            DomainPositionDirection.LongPosition,
            0.25m, 30_000.5m, Now, "future-001", reduceOnly);

    private static FuturesDemoExecutionAccount DemoAccount(
        bool isDemoOnly = true,
        ExchangeKind exchange = ExchangeKind.Kraken) =>
        new(AccountId, exchange, isDemoOnly, new ExchangeCredential("demo-key", "c2VjcmV0"));

    private static KrakenFuturesExecutionAdapter Adapter(
        StubGateway gateway,
        FuturesDemoExecutionAccount? account) =>
        new(gateway, new StubAccountSource(account), new FixedTimeProvider(Now));

    private sealed class StubGateway(FuturesOrderPlacement placement) : IFuturesOrderGateway
    {
        public ExchangeKind Exchange => ExchangeKind.Kraken;
        public int PlaceCalls { get; private set; }
        public FuturesOrderRequest? LastRequest { get; private set; }

        public Task<FuturesOrderPlacement> PlaceAsync(
            ExchangeCredential credential,
            FuturesOrderRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceCalls++;
            LastRequest = request;
            return Task.FromResult(placement);
        }

        public Task<FuturesOrderCancellation> CancelAsync(
            ExchangeCredential credential, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(FuturesOrderQueryOutcome Outcome, FuturesOrderState? Order, string? FailureReason)> QueryAsync(
            ExchangeCredential credential, string clientOrderId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubAccountSource(FuturesDemoExecutionAccount? account) : IFuturesDemoExecutionAccountSource
    {
        public Task<FuturesDemoExecutionAccount?> ResolveAsync(
            Guid futuresDemoAccountId,
            CancellationToken cancellationToken) => Task.FromResult(account);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
