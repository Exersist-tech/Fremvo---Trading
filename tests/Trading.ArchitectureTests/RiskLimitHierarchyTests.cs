using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class RiskLimitHierarchyTests
{
    [Fact]
    public void UserLimitsCannotExceedPlatformCeilings()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new RiskLimitHierarchy(
                platformMaxExposure: 500m,
                platformMaxPositionSize: 200m,
                userMaxExposure: 750m,
                userMaxPositionSize: 100m));

        Assert.Contains("User-configured exposure exceeds the platform ceiling", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveLimitsClampToPlatformCeilings()
    {
        var hierarchy = new RiskLimitHierarchy(
            platformMaxExposure: 500m,
            platformMaxPositionSize: 200m,
            userMaxExposure: 400m,
            userMaxPositionSize: 150m);

        Assert.Equal(400m, hierarchy.EffectiveMaxExposure);
        Assert.Equal(150m, hierarchy.EffectiveMaxPositionSize);
        Assert.Equal(400m, hierarchy.GetEffectiveLimit(RiskLimitType.MaxExposure));
        Assert.Equal(150m, hierarchy.GetEffectiveLimit(RiskLimitType.MaxPositionSize));
    }
}
