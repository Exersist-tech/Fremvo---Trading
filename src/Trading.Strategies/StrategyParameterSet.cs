namespace Trading.Strategies;

public sealed class StrategyParameterSet
{
    private readonly Dictionary<string, StrategyParameterDefinition> _definitions;
    private readonly Dictionary<string, decimal> _values;

    public StrategyParameterSet(IEnumerable<StrategyParameterDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        _definitions = definitions
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in _definitions.Values)
        {
            if (definition.Required)
            {
                _values[definition.Name] = definition.DefaultValue;
            }
        }
    }

    public IReadOnlyDictionary<string, decimal> Values => _values;

    public void Set(string name, decimal value)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Parameter name is required.", nameof(name));
        }

        if (!_definitions.TryGetValue(name, out var definition))
        {
            throw new InvalidOperationException($"Parameter '{name}' is not defined.");
        }

        if (!definition.IsInRange(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"Parameter '{name}' must be within [{definition.Minimum}, {definition.Maximum}].");
        }

        _values[name] = value;
    }

    public decimal Get(string name)
    {
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
}
