using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class RiskLimitHierarchyTests
{
    [Fact]
    public void PlatformCeilingsWinWhenChildrenAreHigher()
    {
        var hierarchy = new RiskLimitHierarchy(
            platformMaxExposure: 500m,
            platformMaxPositionSize: 200m,
            accountMaxExposure: 750m,
            accountMaxPositionSize: 300m,
            userMaxExposure: 700m,
            userMaxPositionSize: 250m,
            strategyMaxExposure: 600m,
            strategyMaxPositionSize: 225m);

        Assert.Equal(500m, hierarchy.EffectiveMaxExposure);
        Assert.Equal(200m, hierarchy.EffectiveMaxPositionSize);
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

    [Fact]
    public void LowestConfiguredChildLimitWins()
    {
        var hierarchy = new RiskLimitHierarchy(
            platformMaxExposure: 500m,
            platformMaxPositionSize: 200m,
            accountMaxExposure: 450m,
            accountMaxPositionSize: 180m,
            userMaxExposure: 400m,
            userMaxPositionSize: 160m,
            strategyMaxExposure: 350m,
            strategyMaxPositionSize: 150m);

        Assert.Equal(350m, hierarchy.EffectiveMaxExposure);
        Assert.Equal(150m, hierarchy.EffectiveMaxPositionSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MandatoryPlatformCeilingsMustBePositive(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RiskLimitHierarchy(value, 1m));
    }

    [Fact]
    public void OptionalLimitsMustBePositiveWhenSpecified()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RiskLimitHierarchy(1m, 1m, userMaxExposure: 0m));
    }
}
