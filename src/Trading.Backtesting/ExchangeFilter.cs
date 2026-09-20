namespace Trading.Backtesting;

public sealed class ExchangeFilter
{
    public ExchangeFilter(
        decimal minNotional,
        decimal minQty,
        decimal tickSize,
        decimal stepSize,
        bool allowPartialFills = true)
    {
        if (minNotional < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minNotional), "Minimum notional cannot be negative.");
        }

        if (minQty < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minQty), "Minimum quantity cannot be negative.");
        }

        if (tickSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tickSize), "Tick size must be positive.");
        }

        if (stepSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(stepSize), "Step size must be positive.");
        }

        MinNotional = minNotional;
        MinQty = minQty;
        TickSize = tickSize;
        StepSize = stepSize;
        AllowPartialFills = allowPartialFills;
    }

    public decimal MinNotional { get; }

    public decimal MinQty { get; }

    public decimal TickSize { get; }

    public decimal StepSize { get; }

    public bool AllowPartialFills { get; }

    public bool IsOrderAllowed(decimal quantity, decimal price)
    {
        if (quantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");
        }

        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        var notional = quantity * price;
        return quantity >= MinQty
            && notional >= MinNotional
            && quantity % StepSize == 0m
            && price % TickSize == 0m;
    }
}
