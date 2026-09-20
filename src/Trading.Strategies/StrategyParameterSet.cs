using System.Collections.ObjectModel;

namespace Trading.Strategies;

public sealed class StrategyParameterSet
{
    private readonly IReadOnlyDictionary<string, StrategyParameterDefinition> _definitions;
    private readonly ReadOnlyDictionary<string, decimal> _values;

    public StrategyParameterSet(
        IEnumerable<StrategyParameterDefinition> definitions,
        IReadOnlyDictionary<string, StrategyParameterValue>? values = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var definitionList = definitions.ToArray();
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

        _definitions = new ReadOnlyDictionary<string, StrategyParameterDefinition>(
            definitionList.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase));

        var suppliedValues = values ?? new Dictionary<string, StrategyParameterValue>(StringComparer.OrdinalIgnoreCase);
        var validatedValues = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplied in suppliedValues)
        {
            if (!_definitions.TryGetValue(supplied.Key, out var definition))
            {
                throw new ArgumentException($"Parameter '{supplied.Key}' is not defined.", nameof(values));
            }

            ValidateValue(definition, supplied.Value, nameof(values));
            validatedValues.Add(definition.Name, supplied.Value.RequireDecimal(definition.Name));
        }

        foreach (var definition in _definitions.Values.Where(definition => definition.Required))
        {
            if (!validatedValues.ContainsKey(definition.Name))
            {
                validatedValues.Add(
                    definition.Name,
                    definition.DefaultValue);
            }
        }

        _values = new ReadOnlyDictionary<string, decimal>(validatedValues);
    }

    public IReadOnlyDictionary<string, decimal> Values => _values;

    public decimal GetDecimal(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Parameter name is required.", nameof(name));
        }

        if (!_values.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException($"Parameter '{name}' was not found.");
        }

        return value;
    }

    public decimal Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Parameter name is required.", nameof(name));
        }

        if (!_values.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException($"Parameter '{name}' was not found.");
        }

        return value;
    }

    public StrategyParameterDefinition GetDefinition(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Parameter name is required.", nameof(name));
        }

        if (!_definitions.TryGetValue(name, out var definition))
        {
            throw new KeyNotFoundException($"Parameter '{name}' was not found.");
        }

        return definition;
    }

    private static void ValidateValue(
        StrategyParameterDefinition definition,
        StrategyParameterValue value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.ValueType != definition.ValueType)
        {
            throw new ArgumentException(
                $"Parameter '{definition.Name}' requires {definition.ValueType} values.",
                parameterName);
        }

        var decimalValue = value.RequireDecimal(definition.Name);
        if (!definition.IsInRange(decimalValue))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Parameter '{definition.Name}' must be within [{definition.Minimum}, {definition.Maximum}].");
        }
    }
}
