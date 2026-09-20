using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class StrategyParameterTests
{
    [Fact]
    public void StrategyParameterDefinitionRejectsInvalidRanges()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyParameterDefinition("fastPeriod", 30m, 10m, 15m, "Fast EMA period"));

        Assert.Equal("minimum", ex.ParamName);
    }

    [Fact]
    public void StrategyParameterSetValidatesRangeAndStoresImmutableValues()
    {
        var definitions = new[]
        {
            new StrategyParameterDefinition("fastPeriod", 8m, 30m, 12m, "Fast EMA period"),
            new StrategyParameterDefinition("slowPeriod", 20m, 80m, 50m, "Slow EMA period")
        };

        var set = new StrategyParameterSet(definitions);

        Assert.Equal(12m, set.GetDecimal("fastPeriod"));

        var configured = new StrategyParameterSet(
            definitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["slowPeriod"] = StrategyParameterValue.FromNumeric(45m)
            });

        Assert.Equal(45m, configured.GetDecimal("slowPeriod"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(
            definitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["fastPeriod"] = StrategyParameterValue.FromNumeric(40m)
            }));
    }
}
