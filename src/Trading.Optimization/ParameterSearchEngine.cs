namespace Trading.Optimization;

using System.Globalization;
using Trading.Strategies;

/// <summary>
/// The only datasets a parameter search may use to select a candidate.
/// Holdout data is deliberately not representable by this type.
/// </summary>
public sealed class ParameterSearchSelection
{
    public ParameterSearchSelection(DatasetSplit training, DatasetSplit validation)
    {
        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(validation);

        if (training.SplitType != DatasetSplitType.Training)
        {
            throw new ArgumentException("Parameter search requires a training split.", nameof(training));
        }

        if (validation.SplitType != DatasetSplitType.Validation)
        {
            throw new ArgumentException("Parameter search requires a validation split.", nameof(validation));
        }

        if (!training.IsAvailableForSelection || !validation.IsAvailableForSelection)
        {
            throw new ArgumentException("Holdout data cannot be used for parameter selection.");
        }

        training.EnsureSameDataset(validation);
        training.ValidateNoFutureLeakage(validation);
        if (!validation.IsTimeOrderedAfter(training))
        {
            throw new ArgumentException(
                "Validation data must be time-ordered after training data to prevent future-data leakage.",
                nameof(validation));
        }

        Training = training;
        Validation = validation;
    }

    public DatasetSplit Training { get; }

    public DatasetSplit Validation { get; }
}

public sealed class ParameterSearchCandidate
{
    public ParameterSearchCandidate(StrategyParameterSet parameters, decimal selectionScore)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        Parameters = parameters;
        SelectionScore = selectionScore;
    }

    public StrategyParameterSet Parameters { get; }

    /// <summary>
    /// A caller-supplied decimal selection metric evaluated only on training and validation data.
    /// It is not a profit claim or a trading recommendation.
    /// </summary>
    public decimal SelectionScore { get; }

    /// <summary>Compatibility alias for callers that previously consumed the generic objective score.</summary>
    public decimal ObjectiveScore => SelectionScore;
}

/// <summary>
/// Deterministic bounded grid search over declared strategy parameters. It neither executes a
/// strategy nor evaluates holdout data; callers provide a decimal scorer for a typed selection.
/// </summary>
public sealed class ParameterSearchEngine
{
    public const int MaximumParameterDefinitions = 16;
    public const int MaximumGridPointsPerParameter = 1_000;
    public const int MaximumCandidateCount = 10_000;

    public static IReadOnlyList<ParameterSearchCandidate> Search(
        ParameterSearchSelection selection,
        IEnumerable<StrategyParameterDefinition> definitions,
        Func<ParameterSearchSelection, StrategyParameterSet, decimal> score,
        int gridPointsPerParameter,
        int maximumCandidateCount)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(score);

        var definitionsList = NormalizeDefinitions(definitions);
        ValidateSearchLimits(gridPointsPerParameter, maximumCandidateCount);

        var ranges = definitionsList
            .Select(definition => (Definition: definition, Values: CreateValues(definition, gridPointsPerParameter)))
            .ToArray();

        var candidateCount = 1;
        foreach (var gridRange in ranges)
        {
            if (candidateCount > maximumCandidateCount / gridRange.Values.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumCandidateCount),
                    $"The grid contains more than the allowed {maximumCandidateCount} candidates.");
            }

            candidateCount *= gridRange.Values.Count;
        }

        var results = new List<ParameterSearchCandidate>(candidateCount);
        SearchCombinations(ranges, 0, new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase), results, selection, score);

        return results
            .OrderByDescending(candidate => candidate.SelectionScore)
            .ThenBy(CreateStableParameterKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static StrategyParameterDefinition[] NormalizeDefinitions(
        IEnumerable<StrategyParameterDefinition> definitions)
    {
        var definitionList = definitions.ToArray();
        if (definitionList.Length == 0)
        {
            throw new ArgumentException("At least one parameter definition is required.", nameof(definitions));
        }

        if (definitionList.Length > MaximumParameterDefinitions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definitions),
                $"At most {MaximumParameterDefinitions} parameter definitions are supported.");
        }

        if (definitionList.Any(definition => definition is null))
        {
            throw new ArgumentException("Parameter definitions cannot contain null values.", nameof(definitions));
        }

        var duplicate = definitionList
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Parameter '{duplicate.Key}' is defined more than once.", nameof(definitions));
        }

        foreach (var definition in definitionList)
        {
            if (definition.ValueType == StrategyParameterValueType.WholeNumber &&
                (decimal.Truncate(definition.Minimum) != definition.Minimum ||
                 decimal.Truncate(definition.Maximum) != definition.Maximum))
            {
                throw new ArgumentException(
                    $"Whole-number parameter '{definition.Name}' must have whole-number bounds.",
                    nameof(definitions));
            }
        }

        return definitionList.OrderBy(definition => definition.Name, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateSearchLimits(int gridPointsPerParameter, int maximumCandidateCount)
    {
        if (gridPointsPerParameter < 2 || gridPointsPerParameter > MaximumGridPointsPerParameter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gridPointsPerParameter),
                $"Grid points per parameter must be between 2 and {MaximumGridPointsPerParameter}.");
        }

        if (maximumCandidateCount < 1 || maximumCandidateCount > MaximumCandidateCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCandidateCount),
                $"Maximum candidate count must be between 1 and {MaximumCandidateCount}.");
        }
    }

    private static List<decimal> CreateValues(
        StrategyParameterDefinition definition,
        int gridPointsPerParameter)
    {
        var values = new List<decimal>(gridPointsPerParameter);
        var span = definition.Maximum - definition.Minimum;

        for (var index = 0; index < gridPointsPerParameter; index++)
        {
            var value = index switch
            {
                0 => definition.Minimum,
                _ when index == gridPointsPerParameter - 1 => definition.Maximum,
                _ => definition.Minimum + (span * index / (gridPointsPerParameter - 1))
            };

            if (definition.ValueType == StrategyParameterValueType.WholeNumber)
            {
                value = decimal.Floor(value);
            }

            if (!definition.IsInRange(value))
            {
                throw new InvalidOperationException($"Generated value for '{definition.Name}' is outside its declared range.");
            }

            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static void SearchCombinations(
        IReadOnlyList<(StrategyParameterDefinition Definition, List<decimal> Values)> ranges,
        int index,
        Dictionary<string, decimal> current,
        ICollection<ParameterSearchCandidate> results,
        ParameterSearchSelection selection,
        Func<ParameterSearchSelection, StrategyParameterSet, decimal> score)
    {
        if (index == ranges.Count)
        {
            var values = new Dictionary<string, StrategyParameterValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var definitionRange in ranges)
            {
                var value = current[definitionRange.Definition.Name];
                values.Add(
                    definitionRange.Definition.Name,
                    definitionRange.Definition.ValueType == StrategyParameterValueType.WholeNumber
                        ? StrategyParameterValue.WholeNumber(value)
                        : StrategyParameterValue.FromNumeric(value));
            }

            var parameterSet = new StrategyParameterSet(ranges.Select(definitionRange => definitionRange.Definition), values);
            results.Add(new ParameterSearchCandidate(parameterSet, score(selection, parameterSet)));
            return;
        }

        var range = ranges[index];
        foreach (var value in range.Values)
        {
            current[range.Definition.Name] = value;
            SearchCombinations(ranges, index + 1, current, results, selection, score);
        }

        current.Remove(range.Definition.Name);
    }

    private static string CreateStableParameterKey(ParameterSearchCandidate candidate) =>
        string.Join(
            "\u001f",
            candidate.Parameters.Values
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => $"{value.Key}\u001e{value.Value.ToString("G29", CultureInfo.InvariantCulture)}"));
}
