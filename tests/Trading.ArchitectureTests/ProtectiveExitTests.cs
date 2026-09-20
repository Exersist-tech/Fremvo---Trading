using Trading.Application.Execution;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Market;
using Trading.Domain.Positions;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers stop and target enforcement on paper positions.
/// </summary>
/// <remarks>
/// A stop that is stored but never acted on is worse than no stop at all: it
/// reads as protection that does not exist. These tests exist to keep the
/// levels enforced rather than decorative.
/// </remarks>
public sealed class ProtectiveExitTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Opened = Now.AddHours(-1);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StrategyId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string Symbol = "XBTUSD";

    private static Candle Bar(
        DateTimeOffset closeTime,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        bool isClosed = true) =>
        new(
            Symbol,
            CandleInterval.OneMinute,
            closeTime.AddMinutes(-1),
            closeTime,
            open,
            high,
            low,
            close,
            volume: 1m,
            isClosed: isClosed,
            isDerived: false);

    private static Position OpenPosition(
        PositionDirection direction = PositionDirection.DirectionLong,
        decimal entry = 30000m,
        decimal quantity = 2m,
        Guid? userId = null) =>
        new(
            Guid.NewGuid(),
            userId ?? UserId,
            StrategyId,
            Symbol,
            direction,
            quantity,
            entry,
            entry,
            Opened);

    private static async Task<(IReadOnlyList<ProtectiveExitFill> Fills, InMemoryPositionRepository Positions, RecordingAudit Audit)>
        EvaluateAsync(Position position, IReadOnlyList<Candle> candles, Guid? evaluateFor = null)
    {
        var positions = new InMemoryPositionRepository();
        await positions.AddAsync(position, CancellationToken.None);

        var audit = new RecordingAudit();
        var evaluator = new ProtectiveExitEvaluator(
            new StubCandles(candles),
            positions,
            audit,
            new FixedTime(Now));

        var fills = await evaluator.EvaluateAsync(evaluateFor ?? position.UserId, CancellationToken.None);
        return (fills, positions, audit);
    }

    [Fact]
    public async Task ALongStopTriggersWhenTheCandleLowReachesIt()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        var (fills, positions, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 29500m, 29600m, 28900m, 29100m)]);

        var fill = Assert.Single(fills);
        Assert.Equal(ProtectiveExitKind.StopLoss, fill.Kind);
        Assert.Equal(29000m, fill.ExitPrice);
        Assert.Equal(-2000m, fill.RealisedPnl);
        Assert.Empty(await positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ALongTargetTriggersWhenTheCandleHighReachesIt()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        var (fills, _, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 30500m, 31200m, 30400m, 31100m)]);

        var fill = Assert.Single(fills);
        Assert.Equal(ProtectiveExitKind.TakeProfit, fill.Kind);
        Assert.Equal(31000m, fill.ExitPrice);
        Assert.Equal(2000m, fill.RealisedPnl);
    }

    [Fact]
    public async Task AShortStopTriggersWhenTheCandleHighReachesIt()
    {
        var position = OpenPosition(PositionDirection.DirectionShort);
        position.SetProtectiveExits(31000m, 29000m, Now);

        var (fills, _, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 30500m, 31500m, 30400m, 31400m)]);

        var fill = Assert.Single(fills);
        Assert.Equal(ProtectiveExitKind.StopLoss, fill.Kind);
        Assert.Equal(31000m, fill.ExitPrice);

        // A short loses as the price rises, so the result must be negative.
        Assert.Equal(-2000m, fill.RealisedPnl);
    }

    [Fact]
    public async Task AShortTargetTriggersWhenTheCandleLowReachesIt()
    {
        var position = OpenPosition(PositionDirection.DirectionShort);
        position.SetProtectiveExits(31000m, 29000m, Now);

        var (fills, _, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 29500m, 29600m, 28800m, 28900m)]);

        var fill = Assert.Single(fills);
        Assert.Equal(ProtectiveExitKind.TakeProfit, fill.Kind);
        Assert.Equal(2000m, fill.RealisedPnl);
    }

    [Fact]
    public async Task OneCandleReachingBothLevelsIsSettledAsTheStop()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        // A candle records no sequence, so the true order is unknowable.
        // Settling this as the target would flatter every result in exactly
        // the case where the truth cannot be recovered.
        var (fills, _, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 30000m, 31500m, 28500m, 30200m)]);

        var fill = Assert.Single(fills);
        Assert.Equal(ProtectiveExitKind.StopLoss, fill.Kind);
        Assert.True(fill.BothLevelsTouched);
    }

    [Fact]
    public async Task AStopReachedNormallyIsNotReportedAsAmbiguous()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        var (fills, _, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 29500m, 29600m, 28900m, 29100m)]);

        Assert.False(Assert.Single(fills).BothLevelsTouched);
    }

    [Fact]
    public async Task AGapBelowALongStopFillsAtTheOpenNotAtTheStop()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        // The market opened at 28000, well below the stop. It was never
        // possible to sell at 29000, so filling there would invent a price.
        var (fills, _, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 28000m, 28100m, 27500m, 27800m)]);

        var fill = Assert.Single(fills);
        Assert.Equal(28000m, fill.ExitPrice);
        Assert.Equal(-4000m, fill.RealisedPnl);
    }

    [Fact]
    public async Task AGapAboveAShortStopFillsAtTheOpenNotAtTheStop()
    {
        var position = OpenPosition(PositionDirection.DirectionShort);
        position.SetProtectiveExits(31000m, 29000m, Now);

        var (fills, _, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 32000m, 32500m, 31900m, 32400m)]);

        Assert.Equal(32000m, Assert.Single(fills).ExitPrice);
    }

    [Fact]
    public async Task AFormingCandleNeverTriggersAnExit()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        // A forming bar's low can still move, so acting on it closes a trade
        // on a level the finished bar may never have reached.
        var (fills, positions, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-1), 29500m, 29600m, 28000m, 28100m, isClosed: false)]);

        Assert.Empty(fills);
        Assert.Single(await positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ACandleThatClosedBeforeThePositionOpenedNeverTriggers()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        // History from before the trade existed must not close it. Otherwise
        // every position with a stop would end the moment it was opened.
        var (fills, positions, _) = await EvaluateAsync(
            position,
            [Bar(Opened.AddMinutes(-5), 29500m, 29600m, 28000m, 28100m)]);

        Assert.Empty(fills);
        Assert.Single(await positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ACandleTouchingNeitherLevelLeavesThePositionOpen()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        var (fills, positions, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 30000m, 30500m, 29500m, 30100m)]);

        Assert.Empty(fills);
        Assert.Single(await positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task APositionWithNoLevelsIsNeverEvaluated()
    {
        var position = OpenPosition();

        var (fills, positions, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 10m, 10m, 10m, 10m)]);

        Assert.Empty(fills);
        Assert.Single(await positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task TheEarliestTriggeringCandleWins()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        // Supplied out of order on purpose: the evaluator must sort by time,
        // not trust the order the venue happened to return.
        var (fills, _, _) = await EvaluateAsync(
            position,
            [
                Bar(Now.AddMinutes(-2), 30500m, 31500m, 30400m, 31400m),
                Bar(Now.AddMinutes(-20), 29500m, 29600m, 28900m, 29100m),
            ]);

        Assert.Equal(ProtectiveExitKind.StopLoss, Assert.Single(fills).Kind);
    }

    [Fact]
    public async Task AMarketDataFailureLeavesThePositionOpen()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        var positions = new InMemoryPositionRepository();
        await positions.AddAsync(position, CancellationToken.None);

        var evaluator = new ProtectiveExitEvaluator(
            new FailingCandles(),
            positions,
            new RecordingAudit(),
            new FixedTime(Now));

        var fills = await evaluator.EvaluateAsync(UserId, CancellationToken.None);

        // Closing on absent data would invent an exit the market never gave.
        Assert.Empty(fills);
        Assert.Single(await positions.ListOpenAsync(UserId, CancellationToken.None));
    }

    [Fact]
    public async Task ATriggeredExitWritesAnAuditEvent()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        var (_, _, audit) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 29500m, 29600m, 28900m, 29100m)]);

        var evt = Assert.Single(audit.Events);
        Assert.Equal("PaperStopLossHit", evt.Action);
        Assert.Contains("No exchange was contacted", evt.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneUsersPositionIsNeverEvaluatedForAnother()
    {
        var position = OpenPosition(userId: OtherUserId);
        position.SetProtectiveExits(29000m, 31000m, Now);

        var (fills, positions, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 29500m, 29600m, 28900m, 29100m)],
            evaluateFor: UserId);

        Assert.Empty(fills);
        Assert.Single(await positions.ListOpenAsync(OtherUserId, CancellationToken.None));
    }

    [Fact]
    public void AStopOnTheProfitableSideOfALongIsRefused()
    {
        var position = OpenPosition();

        // A stop above the entry of a long triggers at once and closes the
        // trade at the opposite of what the stop was for.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => position.SetProtectiveExits(31000m, 32000m, Now));
    }

    [Fact]
    public void ATargetOnTheLosingSideOfALongIsRefused()
    {
        var position = OpenPosition();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => position.SetProtectiveExits(28000m, 29000m, Now));
    }

    [Fact]
    public void AStopOnTheProfitableSideOfAShortIsRefused()
    {
        var position = OpenPosition(PositionDirection.DirectionShort);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => position.SetProtectiveExits(29000m, 28000m, Now));
    }

    [Fact]
    public void ANegativeLevelIsRefused()
    {
        var position = OpenPosition();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => position.SetProtectiveExits(-1m, null, Now));
    }

    [Fact]
    public void ClearingBothLevelsDisarmsThePosition()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);
        Assert.True(position.HasProtectiveExits);

        position.SetProtectiveExits(null, null, Now);

        Assert.False(position.HasProtectiveExits);
        Assert.Null(position.StopLossPrice);
        Assert.Null(position.TakeProfitPrice);
    }

    [Fact]
    public async Task AClosedPositionReportsNoUnrealisedResult()
    {
        var position = OpenPosition();
        position.SetProtectiveExits(29000m, 31000m, Now);

        var (_, positions, _) = await EvaluateAsync(
            position,
            [Bar(Now.AddMinutes(-5), 29500m, 29600m, 28900m, 29100m)]);

        Assert.Empty(await positions.ListOpenAsync(UserId, CancellationToken.None));
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.UnrealizedPnl);
    }

    private sealed class StubCandles : IHistoricalCandleSource
    {
        private readonly IReadOnlyList<Candle> _candles;

        public StubCandles(IReadOnlyList<Candle> candles) => _candles = candles;

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_candles);
    }

    private sealed class FailingCandles : IHistoricalCandleSource
    {
        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default) =>
            throw new MarketDataSourceException("The venue is unreachable.");
    }

    private sealed class RecordingAudit : IAuditEventWriter
    {
        private readonly List<AuditEvent> _events = [];

        public IReadOnlyList<AuditEvent> Events => _events;

        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            _events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
