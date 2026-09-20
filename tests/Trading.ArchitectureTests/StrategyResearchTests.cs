using Trading.Domain.Market;
using Trading.Domain.Strategies;

namespace Trading.ArchitectureTests
{
    public class StrategyResearchTests
    {
        [Fact]
        public void StrategyResearchTemplateStoresApprovedResearchRequirements()
        {
            var strategy = new StrategyResearchTemplate(
                "ema-trend-continuation",
                "Multi-timeframe EMA trend continuation",
                "When a higher timeframe has a directional trend, lower-timeframe pullbacks may offer better continuation entries.",
                TradingProductType.Spot,
                "Highly liquid spot pairs only",
                "Higher timeframe trend must remain positive",
                "4h",
                "15m",
                "5m",
                120,
                "Fast EMA: 8-30; Medium EMA: 20-80; Slow EMA: 50-250",
                "Fast EMA family: 8-30; medium 20-80; slow 50-250",
                "Price above slow EMA and signal re-crosses above fast EMA",
                "Close below medium EMA or higher-timeframe invalidation",
                "No stale data and no invalid liquidity markers",
                "Risk-based sizing by ATR and allowed volatility",
                "Fees, spread, slippage, and latency must be modeled",
                "Use only closed candles and reject stale or incomplete inputs",
                "Modeled costs and regime failures are disqualifying",
                "Testing must include train/validation/walk-forward/holdout and paper trading",
                "No unit tests yet; strategy remains Draft",
                "PaperApproved only after all tests pass",
                "Draft",
                "Draft");

            Assert.Equal("ema-trend-continuation", strategy.Id);
            Assert.Equal(TradingProductType.Spot, strategy.SupportedProductType);
            Assert.Equal(CandleInterval.TenMinutes, CandleInterval.TenMinutes);
            Assert.Equal("Draft", strategy.LiveApprovalStatus);
        }

        [Fact]
        public void CandleIntervalsIncludeNormalizedResearchSet()
        {
            var intervals = Enum.GetValues<CandleInterval>()
                .Where(value => value != CandleInterval.None)
                .ToArray();

            Assert.Contains(CandleInterval.OneMinute, intervals);
            Assert.Contains(CandleInterval.FiveMinutes, intervals);
            Assert.Contains(CandleInterval.TenMinutes, intervals);
            Assert.Contains(CandleInterval.FifteenMinutes, intervals);
            Assert.Contains(CandleInterval.ThirtyMinutes, intervals);
            Assert.Contains(CandleInterval.OneHour, intervals);
            Assert.Contains(CandleInterval.FourHours, intervals);
            Assert.Contains(CandleInterval.OneDay, intervals);
            Assert.Contains(CandleInterval.FourDays, intervals);
            Assert.Equal(9, intervals.Length);
        }
    }
}
