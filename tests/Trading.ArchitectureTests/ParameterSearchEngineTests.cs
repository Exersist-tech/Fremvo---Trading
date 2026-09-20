using Trading.Optimization;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class ParameterSearchEngineTests
{
    [Fact]
    public void ParameterSearchEngineFindsBestScoringCandidate()
    {
        var definitions = new[]
        {
            new StrategyParameterDefinition("lookback", 5m, 20m, 10m, "Lookback period"),
            new StrategyParameterDefinition("threshold", 0.1m, 0.9m, 0.5m, "Threshold")
        };

        var results = ParameterSearchEngine.Search(definitions, parameters =>
        {
            var lookback = parameters.Get("lookback");
            var threshold = parameters.Get("threshold");
            return lookback + threshold;
        }, gridPointsPerParameter: 3);

        Assert.NotEmpty(results);
        Assert.True(results.Count > 1);
        Assert.True(results[0].ObjectiveScore >= results[^1].ObjectiveScore);
        Assert.Contains(results, candidate => candidate.Parameters.Get("lookback") == 20m && candidate.Parameters.Get("threshold") == 0.9m);
    }

    [Fact]
    public void ParameterSearchEngineRejectsEmptyDefinitions()
    {
        var ex = Assert.Throws<ArgumentException>(() => ParameterSearchEngine.Search(Array.Empty<StrategyParameterDefinition>(), _ => 0m));
        Assert.Equal("definitions", ex.ParamName);
    }
}
