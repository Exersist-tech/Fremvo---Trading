using System.Collections.ObjectModel;

namespace Trading.Strategies;

/// <summary>
/// Server composition root for compiled, platform-authored strategies. It
/// accepts strategy objects only; it intentionally exposes no code, script,
/// delegate, expression, or source-text registration API.
/// </summary>
public sealed class ApprovedStrategyCatalog
{
    private readonly IReadOnlyDictionary<StrategyTemplateId, IStrategy> _strategies;

    public ApprovedStrategyCatalog(IEnumerable<IStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        var registered = new Dictionary<StrategyTemplateId, IStrategy>();
        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            if (!registered.TryAdd(strategy.TemplateId, strategy))
            {
                throw new ArgumentException(
                    $"Strategy template '{strategy.TemplateId}' is registered more than once.",
                    nameof(strategies));
            }
        }

        _strategies = new ReadOnlyDictionary<StrategyTemplateId, IStrategy>(registered);
    }

    public IReadOnlyCollection<StrategyTemplateId> TemplateIds => _strategies.Keys.ToArray();

    public IStrategy GetRequired(StrategyTemplateId templateId)
    {
        ArgumentNullException.ThrowIfNull(templateId);
        return _strategies.TryGetValue(templateId, out var strategy)
            ? strategy
            : throw new KeyNotFoundException($"Strategy template '{templateId}' is not registered.");
    }
}
