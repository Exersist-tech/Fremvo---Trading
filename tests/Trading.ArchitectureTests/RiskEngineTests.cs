using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class RiskEngineTests
{
    [Fact]
    public void RiskEngineBlocksWhenTradingIsHaltedOrDataIsStale()
    {
        var engine = new RiskEngine();

        var halted = engine.Evaluate(
            proposedExposure: 50m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 1,
            openPositions: 0,
            maxPositionSize: 100m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: true,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false);

        Assert.False(halted.IsAllowed);
        Assert.Contains(halted.ActiveLimits, limit => limit.Type == RiskLimitType.MaxExposure);

        var stale = engine.Evaluate(
            proposedExposure: 10m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 100m,
            maxNotional: 500m,
            dataIsStale: true,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false);

        Assert.False(stale.IsAllowed);
    }

    [Fact]
    public void RiskEngineRejectsDuplicateAndOversizedOrders()
    {
        var engine = new RiskEngine();

        var duplicate = engine.Evaluate(
            proposedExposure: 30m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 1,
            openPositions: 0,
            maxPositionSize: 25m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: true,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false);

        Assert.False(duplicate.IsAllowed);

        var oversized = engine.Evaluate(
            proposedExposure: 100m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 25m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false);

        Assert.False(oversized.IsAllowed);
    }

    [Fact]
    public void RiskEngineAllowsSafeOrdersWithinLimits()
    {
        var engine = new RiskEngine();

        var allowed = engine.Evaluate(
            proposedExposure: 10m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 1,
            openPositions: 1,
            maxPositionSize: 25m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false);

        Assert.True(allowed.IsAllowed);
    }

    [Fact]
    public void RiskEngineHonorsTradingModeFlagsAndStalenessPolicy()
    {
        var engine = new RiskEngine();
        var tradingMode = new TradingModeFlags { CloseOnlyMode = true };
        var stalenessPolicy = new StalenessPolicy(TimeSpan.FromMinutes(5));

        var closeOnly = engine.Evaluate(
            proposedExposure: 10m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 100m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false,
            tradingMode: tradingMode);

        Assert.False(closeOnly.IsAllowed);
        Assert.Contains(closeOnly.ActiveLimits, limit => limit.Type == RiskLimitType.MaxExposure);
        Assert.Contains("Close-only", closeOnly.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(stalenessPolicy);

        var stale = engine.Evaluate(
            proposedExposure: 5m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 100m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false,
            stalenessPolicy: new StalenessPolicy(TimeSpan.Zero, requiresFreshData: true));

        Assert.False(stale.IsAllowed);
        Assert.Contains("stale", stale.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StalenessPolicyDoesNotBlockOrdersWhenDataIsFresh()
    {
        var engine = new RiskEngine();
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var fresh = engine.Evaluate(
            proposedExposure: 10m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 100m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false,
            stalenessPolicy: new StalenessPolicy(TimeSpan.FromMinutes(5)),
            lastDataUpdateUtc: now.AddMinutes(-1),
            nowUtc: now);

        Assert.True(fresh.IsAllowed);
    }

    [Fact]
    public void StalenessPolicyBlocksOrdersWhenDataIsTooOld()
    {
        var engine = new RiskEngine();
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var expired = engine.Evaluate(
            proposedExposure: 10m,
            currentExposure: 0m,
            dailyPnL: 0m,
            openOrders: 0,
            openPositions: 0,
            maxPositionSize: 100m,
            maxNotional: 500m,
            dataIsStale: false,
            accountIsHalted: false,
            strategyIsHalted: false,
            closeOnlyMode: false,
            reduceOnlyMode: false,
            duplicateOrderDetected: false,
            orderIdempotencyConflict: false,
            marketHalt: false,
            emergencyStop: false,
            stalenessPolicy: new StalenessPolicy(TimeSpan.FromMinutes(5)),
            lastDataUpdateUtc: now.AddMinutes(-30),
            nowUtc: now);

        Assert.False(expired.IsAllowed);
        Assert.Contains("stale", expired.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
