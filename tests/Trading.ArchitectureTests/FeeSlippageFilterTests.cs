using Trading.Backtesting;

namespace Trading.ArchitectureTests;

public sealed class FeeSlippageFilterTests
{
    [Fact]
    public void FeeModelAppliesMakerAndTakerRates()
    {
        var feeModel = new FeeModel(0.001m, 0.002m, 0.10m, 1.00m);

        var makerFee = feeModel.ComputeFee(1000m, isMakerOrder: true);
        var takerFee = feeModel.ComputeFee(1000m, isMakerOrder: false);

        Assert.Equal(1.00m, makerFee);
        Assert.Equal(1.00m, takerFee);
    }

    [Fact]
    public void SlippageModelComputesDeterministicImpact()
    {
        var model = new SlippageModel(0.05m, 0.0005m);
        var slippage = model.ComputeSlippage(100m, 2m);

        Assert.Equal(0.05m + 0.10m, slippage);
    }

    [Fact]
    public void ExchangeFilterRejectsOrdersNotMeetingClockAndQuantityRules()
    {
        var filter = new ExchangeFilter(
            minNotional: 10m,
            minQty: 0.1m,
            tickSize: 0.01m,
            stepSize: 0.05m,
            allowPartialFills: true);

        Assert.True(filter.IsOrderAllowed(1.00m, 11.00m));
        Assert.False(filter.IsOrderAllowed(0.03m, 11.00m));
        Assert.False(filter.IsOrderAllowed(1.00m, 10.975m));
    }
}
