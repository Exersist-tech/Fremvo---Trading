namespace Trading.Strategies;

public sealed class StrategyParameterDefinition
{
    public StrategyParameterDefinition(
        string name,
        decimal minimum,
        decimal maximum,
        decimal defaultValue,
        string description,
        bool required = true)
    {
        ArgumentNullException.ThrowIfNull(description);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Parameter name is required.", nameof(name));
        }

        if (minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "Minimum cannot exceed maximum.");
        }

        if (defaultValue < minimum || defaultValue > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), "Default value must be within the parameter range.");
        }

        Name = name.Trim();
        Minimum = minimum;
        Maximum = maximum;
        DefaultValue = defaultValue;
        Description = description.Trim();
        Required = required;
    }

    public string Name { get; }

    public decimal Minimum { get; }

    public decimal Maximum { get; }

    public decimal DefaultValue { get; }

    public string Description { get; }

    public bool Required { get; }

    public bool IsInRange(decimal value) => value >= Minimum && value <= Maximum;
}
