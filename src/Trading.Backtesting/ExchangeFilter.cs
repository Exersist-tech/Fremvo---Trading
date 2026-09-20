namespace Trading.Backtesting;

public sealed class ExchangeFilter
{
    public ExchangeFilter(
        decimal minNotional,
        decimal minQty,
        decimal tickSize,
        decimal stepSize)
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
    }

    public decimal MinNotional { get; }

    public decimal MinQty { get; }

    public decimal TickSize { get; }

    public decimal StepSize { get; }

    public bool IsOrderAllowed(decimal quantity, decimal price)
        => GetRejectionReason(quantity, price) is null;

    public string? GetRejectionReason(decimal quantity, decimal price)
    {
        if (quantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be negative.");
        }

        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        if (price % TickSize != 0m)
        {
            return $"Execution price {price} does not conform to the configured price tick {TickSize}.";
        }

        if (quantity % StepSize != 0m)
        {
            return $"Execution quantity {quantity} does not conform to the configured quantity step {StepSize}.";
        }

        if (quantity < MinQty)
        {
            return $"Execution quantity {quantity} is below the configured minimum quantity {MinQty}.";
        }

        var notional = quantity * price;
        return notional < MinNotional
            ? $"Execution notional {notional} is below the configured minimum notional {MinNotional}."
            : null;
    }
}
