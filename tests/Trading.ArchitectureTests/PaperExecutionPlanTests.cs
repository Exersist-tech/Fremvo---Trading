using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class PaperExecutionPlanTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryApprovedFamilyProducesTheSameDeterministicInertPlanFromValidEvidence()
    {
        var catalog = new ApprovedPaperExecutionPlanCatalog();

        Assert.Equal(10, catalog.TemplateIds.Count);
        foreach (var templateId in catalog.TemplateIds)
        {
            var proposal = new StrategyAnalysisProposal(
                templateId, Candles()[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 0.5m, "Completed research condition.");

            var first = catalog.GetRequired(templateId).TryCreate(proposal, Candles());
            var second = catalog.GetRequired(templateId).TryCreate(proposal, Candles());

            Assert.NotNull(first);
            Assert.Equal(first!.EntryReferencePrice, second!.EntryReferencePrice);
            Assert.Equal(first.ProtectiveStopPrice, second.ProtectiveStopPrice);
            Assert.Equal(100m, first.EntryReferencePrice);
            Assert.True(first.ProtectiveStopPrice < first.EntryReferencePrice);
            Assert.True(first.ConservativeTargetPrice > first.EntryReferencePrice);
            Assert.Equal(first.AsOfUtc, first.SourceCandles[^1].CloseTimeUtc);
            Assert.Equal(14, first.SourceCandles.Count);
            Assert.Equal(1, first.Provenance.PlanProfileVersion);
            Assert.Contains(templateId.Value, first.Provenance.StrategyPlanProfileIdentity, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RejectsNeutralWarmupUnsafeAndFutureEvidence()
    {
        var template = new StrategyTemplateId("ema-trend-continuation-v1");
        var adapter = new ApprovedPaperExecutionPlanCatalog().GetRequired(template);
        var candles = Candles();
        var proposal = new StrategyAnalysisProposal(template, candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 0.5m, "Condition.");

        Assert.Null(adapter.TryCreate(new StrategyAnalysisProposal(template, proposal.ObservedAtUtc, StrategyAnalysisDirection.Neutral, 0m, "No condition."), candles));
        Assert.Null(adapter.TryCreate(proposal, candles.Take(13).ToArray()));
        Assert.Null(adapter.TryCreate(proposal, candles.Select((candle, index) => index == 13
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume, false, false)
            : candle).ToArray()));
        Assert.Null(adapter.TryCreate(
            new StrategyAnalysisProposal(template, proposal.ObservedAtUtc.AddHours(1), StrategyAnalysisDirection.Bullish, 0.5m, "Future."),
            candles));
    }

    [Fact]
    public void ShortPlanUsesProtectiveStopAndPlanHasNoExecutionSurface()
    {
        var template = new StrategyTemplateId("donchian-breakout-ensemble-v1");
        var candles = Candles();
        var plan = new ApprovedPaperExecutionPlanCatalog().GetRequired(template).TryCreate(
            new StrategyAnalysisProposal(template, candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bearish, 0.5m, "Condition."),
            candles);

        Assert.NotNull(plan);
        Assert.True(plan!.ProtectiveStopPrice > plan.EntryReferencePrice);
        Assert.True(plan.ConservativeTargetPrice < plan.EntryReferencePrice);
        var names = typeof(PaperExecutionPlan).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(names, name => name.Contains("Quantity", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Order", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Intent", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }

    private static Candle[] Candles() =>
        Enumerable.Range(0, 14).Select(index =>
        {
            var open = 90m + index;
            return new Candle("BTC/USD", CandleInterval.OneHour, Start.AddHours(index), Start.AddHours(index + 1),
                open, open + 2m, open - 1m, index == 13 ? 100m : open + 1m, 10m, true, false);
        }).ToArray();
}
