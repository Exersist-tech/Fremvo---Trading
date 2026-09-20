using Trading.Optimization;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class OptimizationPlanValidatorTests
{
    private static readonly StrategyParameterDefinition[] Definitions =
    {
        new("lookback", 5m, 50m, 20m, "Lookback period")
    };

    private static OptimizationPlanRequest Plan(
        int trainFrom, int trainTo,
        int valFrom, int valTo,
        int holdFrom, int holdTo,
        string symbol = "BTCUSDT",
        IEnumerable<StrategyParameterDefinition>? definitions = null) =>
        new(
            symbol,
            new DateTimeOffset(2024, 1, trainFrom, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, trainTo, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, valFrom, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, valTo, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, holdFrom, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, holdTo, 0, 0, 0, TimeSpan.Zero),
            definitions ?? Definitions);

    [Fact]
    public void ValidPlanIsAcceptedButNotExecutable()
    {
        var result = OptimizationPlanValidator.Validate(Plan(1, 10, 10, 20, 20, 30));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.False(result.CanBeExecuted);
    }

    [Fact]
    public void OverlappingValidationWindowIsRejected()
    {
        var result = OptimizationPlanValidator.Validate(Plan(1, 12, 10, 20, 20, 30));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("non-overlapping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HoldoutBeforeValidationIsRejected()
    {
        var result = OptimizationPlanValidator.Validate(Plan(1, 5, 20, 30, 5, 20));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("look-ahead", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingSymbolIsRejected()
    {
        var result = OptimizationPlanValidator.Validate(Plan(1, 10, 10, 20, 20, 30, symbol: "  "));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Symbol is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingParameterDefinitionsIsRejected()
    {
        var result = OptimizationPlanValidator.Validate(
            Plan(1, 10, 10, 20, 20, 30, definitions: Array.Empty<StrategyParameterDefinition>()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("parameter definition", StringComparison.OrdinalIgnoreCase));
    }
}
