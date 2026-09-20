namespace Trading.Strategies;

public sealed class StrategyParameterValue
{
    private StrategyParameterValue(StrategyParameterValueType valueType, decimal value)
    {
        ValueType = valueType;
        Value = value;
    }

    public StrategyParameterValueType ValueType { get; }

    public decimal Value { get; }

    public static StrategyParameterValue FromNumeric(decimal value) =>
        new(StrategyParameterValueType.Numeric, value);

    public static StrategyParameterValue WholeNumber(decimal value)
    {
        if (decimal.Truncate(value) != value)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A whole-number parameter value must not have a fractional component.");
        }

        return new StrategyParameterValue(StrategyParameterValueType.WholeNumber, value);
    }

    public decimal RequireDecimal(string parameterName)
    {
        if (ValueType is not (StrategyParameterValueType.Numeric or StrategyParameterValueType.WholeNumber))
        {
            throw new InvalidOperationException($"Parameter '{parameterName}' has an unsupported value type.");
        }

        return Value;
    }
}
