using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class PaperRiskPositionSizerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SizesLongAndShortWithExactDecimalArithmetic()
    {
        var longResult = PaperRiskPositionSizer.Size(CreateInput(entry: 100m, stop: 98m));
        var shortResult = PaperRiskPositionSizer.Size(CreateInput(entry: 100m, stop: 102m, direction: PaperPositionDirection.Short));

        Assert.True(longResult.IsAccepted);
        Assert.True(shortResult.IsAccepted);
        Assert.Equal(1.25m, longResult.Quantity);
        Assert.Equal(125m, longResult.Notional);
        Assert.Equal(2.5m, longResult.Risk);
        Assert.Equal(longResult.Quantity, shortResult.Quantity);
        Assert.Equal(longResult.Risk, shortResult.Risk);
    }

    [Fact]
    public void UsesPlatformCeilingOrApprovedStrategyCapWhicheverIsLower()
    {
        var platformWins = PaperRiskPositionSizer.Size(CreateInput(policy: DefaultPolicy with { ApprovedStrategyRiskFraction = 0.01m }));
        var strategyWins = PaperRiskPositionSizer.Size(CreateInput(policy: DefaultPolicy with { ApprovedStrategyRiskFraction = 0.001m }));

        Assert.Equal(1.25m, platformWins.Quantity);
        Assert.Equal(0.5m, strategyWins.Quantity);
        Assert.Equal(0.0025m, platformWins.EffectiveLimits!.EffectiveRiskFraction);
        Assert.Equal(0.001m, strategyWins.EffectiveLimits!.EffectiveRiskFraction);
    }

    [Fact]
    public void BlocksCashMinimumsTickAndInvalidProtectiveStop()
    {
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(cash: 0.5m)).IsAccepted);
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(filters: DefaultFilters with { MinimumQuantity = 2m })).IsAccepted);
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(filters: DefaultFilters with { MinimumNotional = 200m })).IsAccepted);
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(entry: 100.001m)).IsAccepted);
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(stop: 100m)).IsAccepted);
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(stop: 100m, direction: PaperPositionDirection.Short)).IsAccepted);
    }

    [Fact]
    public void FloorsQuantityAndNeverExceedsRisk()
    {
        var result = PaperRiskPositionSizer.Size(CreateInput(entry: 100m, stop: 97m, filters: DefaultFilters with { QuantityStep = 0.3m }));

        Assert.True(result.IsAccepted);
        Assert.Equal(0.6m, result.Quantity);
        Assert.Equal(1.8m, result.Risk);
        Assert.True(result.Risk <= result.EffectiveLimits!.EffectiveRiskFraction * 1000m);
        Assert.Equal(0m, result.Quantity % 0.3m);
    }

    [Fact]
    public void MissingOrStaleSnapshotsFailClosed()
    {
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(equity: null)).IsAccepted);
        Assert.False(PaperRiskPositionSizer.Size(CreateInput(marketAt: Now.AddMinutes(-6))).IsAccepted);
        Assert.False(PaperRiskPositionSizer.Size(CreateInput() with { AccountSnapshotAtUtc = null }).IsAccepted);
    }

    [Fact]
    public void AddsRequireFavorableApprovalAndUseReducedRiskWithoutEscalation()
    {
        var denied = PaperRiskPositionSizer.Size(CreateInput(currentQuantity: 1m, currentExposure: 100m));
        var accepted = PaperRiskPositionSizer.Size(CreateInput(currentQuantity: 1m, currentExposure: 100m, favorableAddApproved: true,
            policy: DefaultPolicy with { AdditionRiskFractionMultiplier = 0.5m }));

        Assert.False(denied.IsAccepted);
        Assert.True(accepted.IsAccepted);
        Assert.Equal(0.62m, accepted.Quantity);
        Assert.Equal(0.00125m, accepted.EffectiveLimits!.EffectiveRiskFraction);
    }

    private static readonly PaperExchangeFilters DefaultFilters = new(0.01m, 0.01m, 0.01m, 1m);
    private static readonly PaperWorkerSizingBudget DefaultBudget = new(0.005m, 1_000m, 2_000m, 20m);
    private static readonly PaperRiskSizingPolicy DefaultPolicy = new(0.005m, 0.0025m, 1_000m, 2_000m, 20m, TimeSpan.FromMinutes(5));

    private static PaperRiskSizingInput CreateInput(
        decimal? equity = 1000m, decimal cash = 1000m, decimal entry = 100m, decimal stop = 98m,
        PaperPositionDirection direction = PaperPositionDirection.Long, decimal currentQuantity = 0m, decimal currentExposure = 0m,
        bool favorableAddApproved = false, PaperExchangeFilters? filters = null, PaperRiskSizingPolicy? policy = null,
        DateTimeOffset? marketAt = null, DateTimeOffset? accountAt = null) =>
        new(equity, cash, entry, stop, direction, currentQuantity, currentExposure, currentQuantity > 0m ? direction : null,
            favorableAddApproved, filters ?? DefaultFilters, DefaultBudget, policy ?? DefaultPolicy, marketAt ?? Now, accountAt ?? Now, Now);
}
