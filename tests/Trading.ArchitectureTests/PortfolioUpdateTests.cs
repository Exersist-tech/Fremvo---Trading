using Trading.Domain.Execution;

namespace Trading.ArchitectureTests;

public sealed class PortfolioUpdateTests
{
    private static PortfolioUpdate Update(decimal before, decimal after) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "BTCUSDT",
            before,
            after,
            cashBalanceBefore: 10_000m,
            cashBalanceAfter: 9_500m,
            realizedPnL: 0m,
            fees: 1.25m,
            appliedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void PositionDeltaIsSigned()
    {
        Assert.Equal(0.5m, Update(1.0m, 1.5m).PositionDelta);
        Assert.Equal(-0.5m, Update(1.5m, 1.0m).PositionDelta);
    }

    [Fact]
    public void ReducesExposureUsesAbsoluteSize()
    {
        Assert.True(Update(1.5m, 1.0m).ReducesExposure);
        Assert.False(Update(1.0m, 1.5m).ReducesExposure);

        // A short position shrinking toward zero also reduces exposure.
        Assert.True(Update(-1.5m, -1.0m).ReducesExposure);
        Assert.False(Update(-1.0m, -1.5m).ReducesExposure);
    }

    [Fact]
    public void NegativeFeesAndBalancesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioUpdate(
            Guid.NewGuid(), Guid.NewGuid(), "BTCUSDT", 0m, 1m, 10m, 10m, 0m, -1m, DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioUpdate(
            Guid.NewGuid(), Guid.NewGuid(), "BTCUSDT", 0m, 1m, -1m, 10m, 0m, 1m, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RequiredIdentifiersAreValidated()
    {
        Assert.Throws<ArgumentException>(() => new PortfolioUpdate(
            Guid.Empty, Guid.NewGuid(), "BTCUSDT", 0m, 1m, 10m, 10m, 0m, 1m, DateTimeOffset.UtcNow));

        Assert.Throws<ArgumentException>(() => new PortfolioUpdate(
            Guid.NewGuid(), Guid.NewGuid(), " ", 0m, 1m, 10m, 10m, 0m, 1m, DateTimeOffset.UtcNow));
    }
}
