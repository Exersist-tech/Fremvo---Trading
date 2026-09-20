namespace Trading.Optimization;

using Trading.Strategies;

public sealed class ParameterSearchCandidate
{
    public ParameterSearchCandidate(StrategyParameterSet parameters, decimal objectiveScore)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Parameters = parameters;
        ObjectiveScore = objectiveScore;
    }

    public StrategyParameterSet Parameters { get; }

    public decimal ObjectiveScore { get; }
}

public sealed class ParameterSearchEngine
{
    public static IReadOnlyList<ParameterSearchCandidate> Search(
        IEnumerable<StrategyParameterDefinition> definitions,
        Func<StrategyParameterSet, decimal> objective,
        int gridPointsPerParameter = 5)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(objective);

        var definitionsList = definitions.ToList();
        if (definitionsList.Count == 0)
        {
            throw new ArgumentException("At least one parameter definition is required.", nameof(definitions));
        }

        if (gridPointsPerParameter < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(gridPointsPerParameter), "Grid points per parameter must be at least 2.");
        }

        var ranges = definitionsList
            .Select(definition =>
            {
                var span = definition.Maximum - definition.Minimum;
                var values = new List<decimal>();

                for (var i = 0; i < gridPointsPerParameter; i++)
                {
                    if (i == 0)
                    {
                        values.Add(definition.Minimum);
                        continue;
                    }

                    if (i == gridPointsPerParameter - 1)
                    {
                        values.Add(definition.Maximum);
                        continue;
                    }

                    var ratio = (decimal)i / (gridPointsPerParameter - 1);
                    values.Add(definition.Minimum + (span * ratio));
                }

                return (Definition: definition, Values: values.Distinct().ToList());
            })
            .ToList();

        var results = new List<ParameterSearchCandidate>();
        SearchCombinations(ranges, 0, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase), results, objective);

        return results
            .OrderByDescending(candidate => candidate.ObjectiveScore)
            .ToList();
    }

    private static void SearchCombinations(
        IReadOnlyList<(StrategyParameterDefinition Definition, List<decimal> Values)> ranges,
        int index,
        Dictionary<string, decimal> current,
        ICollection<ParameterSearchCandidate> results,
        Func<StrategyParameterSet, decimal> objective)
    {
        if (index >= ranges.Count)
        {
            var parameterSet = new StrategyParameterSet(
                ranges.Select(range => range.Definition),
                current.ToDictionary(
                    item => item.Key,
                    item => StrategyParameterValue.FromNumeric(item.Value),
                    StringComparer.OrdinalIgnoreCase));

            results.Add(new ParameterSearchCandidate(parameterSet, objective(parameterSet)));
            return;
        }

        var range = ranges[index];
        foreach (var value in range.Values)
        {
            current[range.Definition.Name] = value;
            SearchCombinations(ranges, index + 1, current, results, objective);
        }

        current.Remove(range.Definition.Name);
    }
}
