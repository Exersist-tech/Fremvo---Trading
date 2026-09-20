namespace Trading.Backtesting;

public sealed class SlippageModel
{
    public SlippageModel(decimal fixedSlippage, decimal percentSlippage = 0m)
    {
        if (fixedSlippage < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fixedSlippage), "Fixed slippage cannot be negative.");
        }

        if (percentSlippage < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(percentSlippage), "Percent slippage cannot be negative.");
        }

        FixedSlippage = fixedSlippage;
        PercentSlippage = percentSlippage;
    }

    public decimal FixedSlippage { get; }

    public decimal PercentSlippage { get; }

    public decimal ComputeSlippage(decimal price, decimal quantity)
    {
        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        if (quantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");
        }

        var percentComponent = price * quantity * PercentSlippage;
        return FixedSlippage + percentComponent;
    }
}
