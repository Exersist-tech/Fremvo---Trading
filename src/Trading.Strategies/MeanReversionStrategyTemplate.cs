using Trading.Domain.Strategies;

namespace Trading.Strategies;

public sealed class MeanReversionStrategyTemplate : ApprovedStrategyTemplate
{
    public MeanReversionStrategyTemplate()
        : base(
            "mean-reversion-v1",
            "Mean Reversion",
            TradingProductType.Spot,
            "Relative-value strategy that buys weakness after price retreats materially below a longer-term mean and exits on recovery.",
            "periods: 10-200, zscoreThreshold: 1.5-4.0, minRangePct: 0.25-5.0",
            "EMA(50), RSI(14), mean-reversion close above/near support, volume confirmation",
            "Requires closed candles, a valid mean range, and no stale or incomplete data.")
    {
    }
}
