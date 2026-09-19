namespace Trading.Backtesting;

public sealed class FeeModel
{
    public FeeModel(
        decimal makerFeeRate,
        decimal takerFeeRate,
        decimal minimumFee,
        decimal maximumFee = 0m)
    {
        if (makerFeeRate < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(makerFeeRate), "Maker fee rate cannot be negative.");
        }

        if (takerFeeRate < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(takerFeeRate), "Taker fee rate cannot be negative.");
        }

        if (minimumFee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFee), "Minimum fee cannot be negative.");
        }

        if (maximumFee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFee), "Maximum fee cannot be negative.");
        }

        MakerFeeRate = makerFeeRate;
        TakerFeeRate = takerFeeRate;
        MinimumFee = minimumFee;
        MaximumFee = maximumFee;
    }

    public decimal MakerFeeRate { get; }

    public decimal TakerFeeRate { get; }

    public decimal MinimumFee { get; }

    public decimal MaximumFee { get; }

    public decimal ComputeFee(decimal notional, bool isMakerOrder)
    {
        if (notional < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(notional), "Notional cannot be negative.");
        }

        var rate = isMakerOrder ? MakerFeeRate : TakerFeeRate;
        var computed = notional * rate;

        if (MaximumFee > 0m && computed > MaximumFee)
        {
            return MaximumFee;
        }

        return Math.Max(computed, MinimumFee);
    }
}
