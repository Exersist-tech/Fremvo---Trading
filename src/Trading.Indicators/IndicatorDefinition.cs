namespace Trading.Indicators;

public sealed class IndicatorDefinition
{
    public IndicatorDefinition(string name, string description, string inputSeries, string outputDescription)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(inputSeries);
        ArgumentNullException.ThrowIfNull(outputDescription);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Indicator name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Indicator description is required.", nameof(description));
        }

        Name = name.Trim();
        Description = description.Trim();
        InputSeries = inputSeries.Trim();
        OutputDescription = outputDescription.Trim();
    }

    public string Name { get; }

    public string Description { get; }

    public string InputSeries { get; }

    public string OutputDescription { get; }
}
