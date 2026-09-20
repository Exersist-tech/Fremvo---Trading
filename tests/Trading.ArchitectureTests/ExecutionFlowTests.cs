using Trading.Domain.Execution;
using Trading.Domain.Market;

namespace Trading.ArchitectureTests;

public sealed class ExecutionFlowTests
{
    [Fact]
    public void MarketEventCanFlowToStrategyDecisionAndTradeIntent()
    {
        var qualityFlags = new[] { "closed", "derived" };

        var market = new MarketEvent(
            Guid.NewGuid(),
            "BTCUSDT",
            CandleInterval.FiveMinutes,
            DateTimeOffset.UtcNow,
            105m,
            12.5m,
            true,
            qualityFlags);

        var decision = new StrategyDecision(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "BTCUSDT",
            SignalDirection.Buy,
            0.85m,
            DateTimeOffset.UtcNow,
            "Breakout confirmed with strong close above prior range.");

        var intent = new TradeIntent(
            Guid.NewGuid(),
            decision.StrategyId,
            market.Symbol,
            TradeDirection.Buy,
            0.75m,
            106m,
            DateTimeOffset.UtcNow,
            reduceOnly: false,
            closeOnly: false);

        Assert.Equal("BTCUSDT", market.Symbol);
        Assert.Equal(SignalDirection.Buy, decision.Direction);
        Assert.Equal(TradeDirection.Buy, intent.Direction);
    }

    [Fact]
    public void RiskEvaluationBlocksUnapprovedExposure()
    {
        var intent = new TradeIntent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "ETHUSDT",
            TradeDirection.Sell,
            2m,
            4000m,
            DateTimeOffset.UtcNow,
            reduceOnly: false,
            closeOnly: false);

        var risk = new RiskEvaluation(
            Guid.NewGuid(),
            intent.Id,
            false,
            1000m,
            300m,
            DateTimeOffset.UtcNow,
            "Exposure exceeds configured ceiling.");

        var command = new ExecutionCommand(
            Guid.NewGuid(),
            intent.Id,
            intent.Symbol,
            intent.Direction,
            intent.Quantity,
            intent.LimitPrice,
            DateTimeOffset.UtcNow,
            "client-eth-1",
            isPaperOnly: true);

        Assert.False(risk.IsAllowed);
        Assert.True(command.IsPaperOnly);
    }
}
