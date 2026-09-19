using Trading.Domain.Strategies;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class StrategyTemplateTests
{
    [Fact]
    public void MomentumBreakoutTemplateIsApprovedAndValid()
    {
        var template = new MomentumBreakoutStrategyTemplate();

        Assert.Equal("momentum-breakout-v1", template.Id);
        Assert.Equal("Momentum Breakout", template.Name);
        Assert.Equal(TradingProductType.Spot, template.SupportedProductType);
        Assert.False(string.IsNullOrWhiteSpace(template.Description));
    }
}
