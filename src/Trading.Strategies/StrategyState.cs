using System.Collections.ObjectModel;

namespace Trading.Strategies;

public sealed class StrategyState
{
    public StrategyState(
        StrategyTemplateId templateId,
        long revision,
        DateTimeOffset lastEvaluatedCloseUtc,
        IReadOnlyDictionary<string, decimal>? values = null)
    {
        ArgumentNullException.ThrowIfNull(templateId);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        if (lastEvaluatedCloseUtc == default)
        {
            throw new ArgumentException("Last evaluated close timestamp is required.", nameof(lastEvaluatedCloseUtc));
        }

        var copiedValues = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? new Dictionary<string, decimal>())
        {
            if (string.IsNullOrWhiteSpace(value.Key))
            {
                throw new ArgumentException("State value names are required.", nameof(values));
            }

            if (!copiedValues.TryAdd(value.Key.Trim(), value.Value))
            {
                throw new ArgumentException($"State value '{value.Key}' occurs more than once.", nameof(values));
            }
        }

        TemplateId = templateId;
        Revision = revision;
        LastEvaluatedCloseUtc = lastEvaluatedCloseUtc.ToUniversalTime();
        Values = new ReadOnlyDictionary<string, decimal>(copiedValues);
    }

    public StrategyTemplateId TemplateId { get; }

    public long Revision { get; }

    public DateTimeOffset LastEvaluatedCloseUtc { get; }

    public IReadOnlyDictionary<string, decimal> Values { get; }
}
