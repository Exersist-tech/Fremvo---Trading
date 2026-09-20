using Trading.Domain.Strategies;

namespace Trading.Strategies;

public sealed class MomentumBreakoutStrategyTemplate : ApprovedStrategyTemplate
{
    public MomentumBreakoutStrategyTemplate()
        : base(
            "momentum-breakout-v1",
            "Momentum Breakout",
            TradingProductType.Spot,
            "Trend continuation strategy that reacts to a confirmed breakout from recent range highs.",
            "periods: 5-50, breakoutThresholdPct: 0.5-3.0, volumeMultiplier: 1.0-3.0",
            "EMA(20), breakout above prior swing high, volume expansion",
            "Requires closed candles, valid range high, and no stale market data.")
    {
    }
}
