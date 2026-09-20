namespace Trading.Indicators;

public interface IIndicatorCalculator
{
    string Name { get; }

    IndicatorDefinition Definition { get; }

    decimal Calculate(IReadOnlyList<decimal> values);
}
