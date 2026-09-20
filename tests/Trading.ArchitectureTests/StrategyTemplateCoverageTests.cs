using Trading.Strategies;
using Trading.Domain.Strategies;

namespace Trading.ArchitectureTests;

public sealed class StrategyTemplateCoverageTests
{
    [Fact]
    public void ApprovedStrategyTemplatesStayWithinSupportedProductTypes()
    {
        var trend = new MomentumBreakoutStrategyTemplate();
        var meanReversion = new MeanReversionStrategyTemplate();

        Assert.Equal(TradingProductType.Spot, trend.SupportedProductType);
        Assert.Equal(TradingProductType.Spot, meanReversion.SupportedProductType);
        Assert.Contains("momentum", trend.Id, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mean", meanReversion.Id, StringComparison.OrdinalIgnoreCase);
    }
}
