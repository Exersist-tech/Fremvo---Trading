using Trading.Optimization;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class ParameterSearchEngineTests
{
    [Fact]
    public void SearchEnumeratesTheExactGridWithDecimalScores()
    {
        var selection = CreateSelection();
        var definitions = new[]
        {
            new StrategyParameterDefinition("threshold", 0.1m, 0.9m, 0.5m, "Threshold"),
            new StrategyParameterDefinition("lookback", 5m, 20m, 10m, "Lookback period")
        };

        var results = ParameterSearchEngine.Search(
            selection,
            definitions,
            (_, parameters) => parameters.Get("lookback") + parameters.Get("threshold"),
            gridPointsPerParameter: 3,
            maximumCandidateCount: 9);

        Assert.Equal(9, results.Count);
        Assert.All(results, result => Assert.IsType<decimal>(result.SelectionScore));
        Assert.Contains(results, candidate => candidate.Parameters.Get("lookback") == 20m && candidate.Parameters.Get("threshold") == 0.9m);
        Assert.All(results, candidate =>
        {
            Assert.InRange(candidate.Parameters.Get("lookback"), 5m, 20m);
            Assert.InRange(candidate.Parameters.Get("threshold"), 0.1m, 0.9m);
        });
    }

    [Fact]
    public void SearchRejectsCombinatorialExplosionBeforeScoring()
    {
        var definitions = Enumerable.Range(1, 3)
            .Select(index => new StrategyParameterDefinition($"parameter{index}", 0m, 1m, 0m, "Bounded"))
            .ToArray();
        var calls = 0;

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ParameterSearchEngine.Search(
            CreateSelection(),
            definitions,
            (_, _) =>
            {
                calls++;
                return 0m;
            },
            gridPointsPerParameter: 3,
            maximumCandidateCount: 26));

        Assert.Equal("maximumCandidateCount", exception.ParamName);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SearchUsesStableOrdinalOrderingForTiedScores()
    {
        var definitions = new[]
        {
            new StrategyParameterDefinition("zeta", 0m, 1m, 0m, "Zeta"),
            new StrategyParameterDefinition("alpha", 0m, 1m, 0m, "Alpha")
        };

        var first = ParameterSearchEngine.Search(CreateSelection(), definitions, (_, _) => 1.5m, 2, 4);
        var second = ParameterSearchEngine.Search(CreateSelection(), definitions.Reverse(), (_, _) => 1.5m, 2, 4);

        Assert.Equal(
            first.Select(CandidateKey),
            second.Select(CandidateKey));
        var expectedOrder = new[] { "alpha=0,zeta=0", "alpha=0,zeta=1", "alpha=1,zeta=0", "alpha=1,zeta=1" };
        Assert.Equal(expectedOrder, first.Select(CandidateKey));
    }

    [Fact]
    public void SearchRejectsHoldoutAndMismatchedOrFutureSelectionSplits()
    {
        var dataset = DatasetSplitTestFactory.Create();
        var training = Split("training", DatasetSplitType.Training, dataset, 1, 10);
        var holdout = Split("holdout", DatasetSplitType.Holdout, dataset, 20, 30);
        var wrongDataset = Split("validation", DatasetSplitType.Validation, DatasetSplitTestFactory.Create(symbol: "ETHUSDT"), 10, 20);
        var futureTraining = Split("future-training", DatasetSplitType.Training, dataset, 20, 30);
        var earlierValidation = Split("earlier-validation", DatasetSplitType.Validation, dataset, 10, 20);

        Assert.Throws<ArgumentException>(() => new ParameterSearchSelection(training, holdout));
        Assert.Throws<ArgumentException>(() => new ParameterSearchSelection(training, wrongDataset));
        Assert.Throws<ArgumentException>(() => new ParameterSearchSelection(futureTraining, earlierValidation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchRejectsNullOrEmptyDefinitions(bool includeNull)
    {
        IEnumerable<StrategyParameterDefinition> definitions = includeNull
            ? new StrategyParameterDefinition[] { null! }
            : Array.Empty<StrategyParameterDefinition>();

        Assert.Throws<ArgumentException>(() => ParameterSearchEngine.Search(
            CreateSelection(),
            definitions,
            (_, _) => 0m,
            gridPointsPerParameter: 2,
            maximumCandidateCount: 1));
    }

    [Fact]
    public void SearchGeneratesOnlyWholeNumbersForWholeNumberDefinitions()
    {
        var definitions = new[]
        {
            new StrategyParameterDefinition(
                "lookback",
                5m,
                20m,
                10m,
                "Lookback period",
                valueType: StrategyParameterValueType.WholeNumber)
        };

        var results = ParameterSearchEngine.Search(CreateSelection(), definitions, (_, _) => 0m, 4, 4);

        Assert.All(results, result => Assert.Equal(decimal.Truncate(result.Parameters.Get("lookback")), result.Parameters.Get("lookback")));
    }

    private static ParameterSearchSelection CreateSelection()
    {
        var dataset = DatasetSplitTestFactory.Create();
        return new ParameterSearchSelection(
            Split("training", DatasetSplitType.Training, dataset, 1, 10),
            Split("validation", DatasetSplitType.Validation, dataset, 10, 20));
    }

    private static DatasetSplit Split(
        string id,
        DatasetSplitType type,
        Trading.Backtesting.HistoricalDataset dataset,
        int fromDay,
        int toDay) =>
        new(
            id,
            type,
            dataset,
            new DateTimeOffset(2024, 1, fromDay, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 1, toDay, 0, 0, 0, TimeSpan.Zero),
            1);

    private static string CandidateKey(ParameterSearchCandidate candidate) =>
        string.Join(",", candidate.Parameters.Values
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => $"{value.Key}={value.Value}"));
}
