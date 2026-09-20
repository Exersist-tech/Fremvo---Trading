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
}