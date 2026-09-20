namespace Trading.Strategies;

public sealed class StrategyTemplateId : IEquatable<StrategyTemplateId>
{
    public StrategyTemplateId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Strategy template id is required.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public bool Equals(StrategyTemplateId? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as StrategyTemplateId);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public override string ToString() => Value;
}
