using Trading.Indicators;

namespace Trading.ArchitectureTests;

public sealed class IndicatorCalculationTests
{
    [Fact]
    public void SimpleMovingAverageCalculatesExpectedValue()
    {
        var calculator = new SimpleMovingAverageCalculator(3);
        var values = new[] { 10m, 12m, 14m, 16m };

        var result = calculator.Calculate(values);

        Assert.Equal(12m, result);
    }

    [Fact]
    public void ExponentialMovingAverageCalculatesExpectedValue()
    {
        var calculator = new ExponentialMovingAverageCalculator(3);
        var values = new[] { 10m, 12m, 14m, 16m };

        var result = calculator.Calculate(values);

        Assert.Equal(14.25m, result, 2);
    }
}
