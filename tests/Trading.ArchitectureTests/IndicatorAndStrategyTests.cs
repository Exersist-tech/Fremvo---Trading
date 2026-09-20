using Trading.Domain.Strategies;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class IndicatorAndStrategyTests
{
    [Fact]
    public void ApprovedStrategyTemplateStoresOnlyApprovedParameters()
    {
        var template = new ApprovedStrategyTemplate(
            "sma-trend",
            "SMA trend",
            TradingProductType.Spot,
            "Uses SMA crossover on liquid spot pairs only.",
            "fast: 8-30; slow: 20-80",
            "fast above slow",
            "market regime invalid when stale");

        Assert.Equal("sma-trend", template.Id);
        Assert.Equal(TradingProductType.Spot, template.SupportedProductType);
        Assert.Equal("fast: 8-30; slow: 20-80", template.ParameterBounds);
    }
}
