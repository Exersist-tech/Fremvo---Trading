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

        return GetPriceAdjustment(price) * quantity;
    }

    public decimal GetExecutionPrice(decimal referencePrice, bool isBuy)
    {
        if (referencePrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(referencePrice), "Price must be positive.");
        }

        var adjustment = GetPriceAdjustment(referencePrice);
        var executionPrice = isBuy ? referencePrice + adjustment : referencePrice - adjustment;
        if (executionPrice <= 0m)
        {
            throw new InvalidOperationException("Adverse sell slippage makes the execution price non-positive.");
        }

        return executionPrice;
    }

    public static SlippageModel Zero { get; } = new(0m);

    private decimal GetPriceAdjustment(decimal price) => FixedSlippage + (price * PercentSlippage);
}
