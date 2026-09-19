using Trading.Domain.Strategies;

namespace Trading.Strategies;

public class ApprovedStrategyTemplate
{
    public ApprovedStrategyTemplate(
        string id,
        string name,
        TradingProductType supportedProductType,
        string description,
        string parameterBounds,
        string requiredSignals,
        string invalidationRules)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(parameterBounds);
        ArgumentNullException.ThrowIfNull(requiredSignals);
        ArgumentNullException.ThrowIfNull(invalidationRules);

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Strategy id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Strategy name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.", nameof(description));
        }

        Id = id.Trim();
        Name = name.Trim();
        SupportedProductType = supportedProductType;
        Description = description.Trim();
        ParameterBounds = parameterBounds.Trim();
        RequiredSignals = requiredSignals.Trim();
        InvalidationRules = invalidationRules.Trim();
    }

    public string Id { get; }

    public string Name { get; }

    public TradingProductType SupportedProductType { get; }

    public string Description { get; }

    public string ParameterBounds { get; }

    public string RequiredSignals { get; }

    public string InvalidationRules { get; }
}
