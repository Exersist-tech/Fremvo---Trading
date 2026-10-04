using Trading.Domain.Orders;
using Trading.Domain.Positions;

namespace Trading.ArchitectureTests;

public sealed class OrderPositionStateTests
{
    [Fact]
    public void OrderTransitionsMustBeValid()
    {
        var order = new Order(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Limit,
            1.5m,
            100m,
            DateTimeOffset.UtcNow,
            "client-order-123");

        order.MarkAccepted();
        Assert.Equal(OrderState.Accepted, order.State);

        order.MarkFilled();
        Assert.Equal(OrderState.Filled, order.State);
    }

    [Fact]
    public void PositionTracksMarkPriceAndReduction()
    {
        var position = new Position(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "BTCUSDT",
            PositionDirection.DirectionLong,
            2m,
            100m,
            100m,
            DateTimeOffset.UtcNow);

        position.UpdateMarkPrice(110m);
        Assert.Equal(20m, position.UnrealizedPnl);

        position.Reduce(1m);
        Assert.Equal(1m, position.Quantity);
        Assert.NotEqual(PositionStatus.Flat, position.Status);

        position.Reduce(1m);
        Assert.Equal(PositionStatus.Flat, position.Status);
    }

    [Fact]
    public void AdditionalObservedFillAdjustsQuantityAndWeightedCostWithoutUsingLimitPrice()
    {
        var position = new Position(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "BTCUSD",
            PositionDirection.DirectionLong, 2m, 100m, 110m, DateTimeOffset.UtcNow);

        position.Increase(1m, 106m);

        Assert.Equal(3m, position.Quantity);
        Assert.Equal(102m, position.EntryPrice);
        Assert.Equal(24m, position.UnrealizedPnl);
        Assert.Equal(1, position.Version);
        Assert.Throws<ArgumentOutOfRangeException>(() => position.Increase(1m, 0m));
        position.Reduce(3m);
        Assert.Throws<InvalidOperationException>(() => position.Increase(1m, 106m));
    }
}