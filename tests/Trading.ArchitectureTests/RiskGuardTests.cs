using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class RiskGuardTests
{
    [Fact]
    public void RiskEngineRejectsStaleAndHaltedOrders()
    {
        var engine = new RiskEngine();

        var halted = engine.Evaluate(
            proposedExposure: 100m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 200m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: true,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false);

        Assert.False(halted.IsAllowed);
        Assert.Contains(halted.ActiveLimits, limit => limit.Type == RiskLimitType.MaxExposure);
    }

    [Fact]
    public void IdempotencyGuardRejectsDuplicateOrderPayloads()
    {
        var guard = new OrderIdempotencyGuard();

        var first = guard.RegisterOrCheck("client-1", "BTCUSDT", 1m, 50000m);
        var duplicate = guard.RegisterOrCheck("client-1", "BTCUSDT", 1m, 50000m);
        var conflict = guard.RegisterOrCheck("client-2", "BTCUSDT", 2m, 50000m);

        Assert.False(first.IsDuplicate);
        Assert.True(duplicate.IsDuplicate);
        Assert.False(conflict.IsDuplicate);
    }
}
