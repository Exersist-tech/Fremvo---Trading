using System.Globalization;
using Trading.Domain.Market;
using Trading.Indicators;
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

        Assert.Equal(16, catalog.TemplateIds.Count);
        Assert.Contains(new StrategyTemplateId("three-swing-channel-divergence-v1"), catalog.TemplateIds);
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

    [Fact]
    public void RelativeStrengthStructureUsesOnlyTheClosedFourHourSwingAndChannelBeforeTheConfirmedHour()
    {
        var (proposal, confirmation, setup) = RelativeEvidence();
        var adapter = ApprovedPaperExecutionPlanCatalog.RelativeStrengthStructure;
        var plan = adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 5, 14, .2m, 20, 1m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(setup).Value;
        Assert.NotNull(atr);
        Assert.Equal(100m, plan.EntryReferencePrice);
        Assert.Equal(98m - atr!.Value * .2m, plan.ProtectiveStopPrice);
        Assert.Equal(104m, plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(65, plan.SourceCandles.Count);
        Assert.Equal(setup[^1].CloseTimeUtc, plan.SourceCandles[^1].CloseTimeUtc);
        Assert.Equal(proposal.ObservedAtUtc, plan.AsOfUtc);
        Assert.Contains("estimated gross reward/risk", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 5, 14, .2m, 20, 1m)!
                .Provenance.ResearchEvidenceId);
        Assert.Null(adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 5, 14, .2m, 5, 1m));
        Assert.Null(adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 5, 14, .2m, 20, 2m));
        Assert.True(adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 5, 14, .2m, 20, .5m)!
            .ProtectiveStopPrice > adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 10, 14, .2m, 20, .5m)!
            .ProtectiveStopPrice);
    }

    [Fact]
    public void RelativeStrengthStructureRejectsGapsWrongIntervalsFutureAndInvalidRisk()
    {
        var (proposal, confirmation, setup) = RelativeEvidence();
        var adapter = ApprovedPaperExecutionPlanCatalog.RelativeStrengthStructure;
        PaperExecutionPlan? Create(IReadOnlyList<Candle> hours, IReadOnlyList<Candle> fourHours) =>
            adapter.TryCreateRelativeStrengthStructure(proposal, hours, fourHours, 5, 14, .2m, 20, 1m);

        Assert.Null(Create(confirmation.Skip(1).ToArray(), setup));
        Assert.Null(Create(confirmation.Select((candle, index) => index == 5
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1), closeTime: candle.CloseTimeUtc.AddMinutes(1))
            : candle).ToArray(), setup));
        Assert.Null(Create(confirmation.Select((candle, index) => index == 5
            ? Copy(candle, closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray(), setup));
        Assert.Null(Create(confirmation.Select((candle, index) => index == 5
            ? Copy(candle, interval: CandleInterval.FiveMinutes) : candle).ToArray(), setup));
        Assert.Null(Create(confirmation.Select((candle, index) => index == 13
            ? Copy(candle, closed: false) : candle).ToArray(), setup));
        Assert.Null(Create(confirmation, setup.TakeLast(13).ToArray()));
        Assert.Null(Create(confirmation, setup.Select((candle, index) => index == 5
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1), closeTime: candle.CloseTimeUtc.AddMinutes(1))
            : candle).ToArray()));
        Assert.Null(Create(confirmation, setup.Select((candle, index) => index == 5
            ? Copy(candle, closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(confirmation, setup.Select((candle, index) => index == 5
            ? Copy(candle, interval: CandleInterval.OneHour) : candle).ToArray()));
        Assert.Null(Create(confirmation, setup.Select((candle, index) => index == 5
            ? Copy(candle, symbol: "XBT/EUR") : candle).ToArray()));
        Assert.Null(Create(confirmation, setup.Select((candle, index) => index == 50
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(confirmation, setup.Select((candle, index) => index == 50
            ? Copy(candle, low: 0.01m) : candle).ToArray()));
        Assert.Null(adapter.TryCreateRelativeStrengthStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            confirmation, setup, 5, 14, .2m, 20, 1m));
        Assert.Null(adapter.TryCreateRelativeStrengthStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, setup[^1].CloseTimeUtc,
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            confirmation, setup, 5, 14, .2m, 20, 1m));
        Assert.Null(adapter.TryCreateRelativeStrengthStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc.AddHours(1),
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            confirmation, setup, 5, 14, .2m, 20, 1m));
        Assert.Null(adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 2, 14, .2m, 20, 1m));
    }

    [Fact]
    public void DonchianStructureFreezesPriorRangeAndUsesTheSavedGrossRiskMultiple()
    {
        var candles = DonchianEvidence();
        var proposal = new StrategyAnalysisProposal(new StrategyTemplateId("donchian-breakout-ensemble-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed channel breakout.");
        var adapter = ApprovedPaperExecutionPlanCatalog.DonchianStructure;

        var plan = adapter.TryCreateDonchianStructure(proposal, candles, 20, 14, .2m, 1.5m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value;
        Assert.NotNull(atr);
        var expectedStop = candles.Take(20).Min(candle => candle.Low) - atr!.Value * .2m;
        Assert.Equal(102m, plan.EntryReferencePrice);
        Assert.Equal(expectedStop, plan.ProtectiveStopPrice);
        Assert.Equal(102m + (102m - expectedStop) * 1.5m, plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(21, plan.SourceCandles.Count);
        Assert.Contains("Frozen prior 20-candle low", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateDonchianStructure(proposal, candles, 20, 14, .2m, 1.5m)!
                .Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateDonchianStructure(proposal, candles, 20, 14, .3m, 1.5m)!
            .ProtectiveStopPrice < plan.ProtectiveStopPrice);
        Assert.True(adapter.TryCreateDonchianStructure(proposal, candles, 20, 14, .2m, 2m)!
            .ConservativeTargetPrice > plan.ConservativeTargetPrice);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void DonchianStructureRejectsGapsUnsafeHistoryAbsentBreakAndInvalidRisk()
    {
        var candles = DonchianEvidence();
        var adapter = ApprovedPaperExecutionPlanCatalog.DonchianStructure;
        var proposal = new StrategyAnalysisProposal(new StrategyTemplateId("donchian-breakout-ensemble-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Breakout.");
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history) =>
            adapter.TryCreateDonchianStructure(proposal, history, 20, 14, .2m, 1.5m);

        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 5
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 20
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 20
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 99m, 101m, 98m, 100m, candle.Volume, true, false)
            : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 5
            ? Copy(candle, symbol: "XBT/EUR") : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 10
            ? Copy(candle, low: .01m) : candle).ToArray()));
        Assert.Null(adapter.TryCreateDonchianStructure(proposal, candles, 20, 14, .2m, .5m));
        Assert.Null(adapter.TryCreateDonchianStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, "Separate Futures proof required."),
            candles, 20, 14, .2m, 1.5m));
    }

    [Fact]
    public void BollingerStructureFreezesTheExcursionStopAndConfiguredMiddleBandTarget()
    {
        var candles = BollingerEvidence();
        var proposal = new StrategyAnalysisProposal(new StrategyTemplateId("bollinger-mean-reversion-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed band re-entry.");
        var adapter = ApprovedPaperExecutionPlanCatalog.BollingerStructure;

        var plan = adapter.TryCreateBollingerStructure(proposal, candles, 20, 2m, 14, .2m, .8m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value;
        Assert.NotNull(atr);
        Assert.Equal(Math.Min(candles[^2].Low, candles[^1].Low) - atr!.Value * .2m,
            plan.ProtectiveStopPrice);
        Assert.Equal(candles.TakeLast(20).Average(candle => candle.Close),
            plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(21, plan.SourceCandles.Count);
        Assert.Contains("Frozen excursion/re-entry low", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateBollingerStructure(proposal, candles, 20, 2m, 14, .2m, .8m)!
                .Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateBollingerStructure(proposal, candles, 20, 2m, 14, .3m, .5m)!
            .ProtectiveStopPrice < plan.ProtectiveStopPrice);
        Assert.Null(adapter.TryCreateBollingerStructure(proposal, candles, 20, 2m, 14, .2m, 1m));
        Assert.Null(adapter.TryCreateBollingerStructure(proposal, candles, 20, 2m, 14, .2m, 1.5m));
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void BollingerStructureRejectsUnsafeGappedUnconfirmedAndFutureEvidence()
    {
        var candles = BollingerEvidence();
        var adapter = ApprovedPaperExecutionPlanCatalog.BollingerStructure;
        var proposal = new StrategyAnalysisProposal(new StrategyTemplateId("bollinger-mean-reversion-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed re-entry.");
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history) =>
            adapter.TryCreateBollingerStructure(proposal, history, 20, 2m, 14, .2m, .8m);

        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 5
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 19
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 19
            ? Copy(candle, symbol: "ETH/EUR") : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 19
            ? Copy(candle, low: .01m) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 19
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 100m, 101m, 99m, 100m, 10m, true, false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 20
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 92m, 101m, 92m, 100m, 10m, true, false) : candle).ToArray()));
        Assert.Null(adapter.TryCreateBollingerStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            candles, 20, 2m, 14, .2m, .8m));
        Assert.Null(adapter.TryCreateBollingerStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc.AddMinutes(15),
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            candles, 20, 2m, 14, .2m, .8m));
        Assert.Null(adapter.TryCreateBollingerStructure(proposal, candles, 20, 2m, 14, .2m, 2.1m));
    }

    [Fact]
    public void RsiPullbackStructureFreezesTheActualSignalSwingAndTarget()
    {
        var candles = RsiPullbackEvidence();
        var proposal = new StrategyAnalysisProposal(new StrategyTemplateId("rsi-pullback-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "RSI turn and resumption.");
        var adapter = ApprovedPaperExecutionPlanCatalog.RsiPullbackStructure;

        var plan = adapter.TryCreateRsiPullbackStructure(proposal, candles, 5, 14, 30m, 45m,
            20, 20, 14, .2m, 1.5m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value;
        Assert.NotNull(atr);
        var swing = candles.TakeLast(5).Min(candle => candle.Low);
        var expectedStop = swing - atr!.Value * .2m;
        Assert.Equal(expectedStop, plan.ProtectiveStopPrice);
        Assert.Equal(candles[^1].Close + (candles[^1].Close - expectedStop) * 1.5m,
            plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(320, plan.SourceCandles.Count);
        Assert.Contains("Frozen 5-candle pullback low", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateRsiPullbackStructure(proposal, candles, 5, 14, 30m, 45m,
                20, 20, 14, .2m, 1.5m)!.Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateRsiPullbackStructure(proposal, candles, 5, 14, 30m, 45m,
            20, 20, 14, .3m, 1.5m)!.ProtectiveStopPrice < expectedStop);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void RsiPullbackStructureRejectsUnattestedSetupUnsafeHistoryAndBearishSpot()
    {
        var candles = RsiPullbackEvidence();
        var proposal = new StrategyAnalysisProposal(new StrategyTemplateId("rsi-pullback-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "RSI turn.");
        var adapter = ApprovedPaperExecutionPlanCatalog.RsiPullbackStructure;
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history) =>
            adapter.TryCreateRsiPullbackStructure(proposal, history, 5, 14, 30m, 45m,
                20, 20, 14, .2m, 1.5m);

        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 12
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 317
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 318
            ? Copy(candle, symbol: "ETH/EUR") : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 315
            ? Copy(candle, low: 0m) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 193m, 196m, 192m, 195m, 100m, true, false) : candle).ToArray()));
        Assert.Null(adapter.TryCreateRsiPullbackStructure(proposal, candles, 5, 14, 40m, 50m,
            20, 20, 14, .2m, 1.5m));
        Assert.Null(adapter.TryCreateRsiPullbackStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            candles, 5, 14, 30m, 45m, 20, 20, 14, .2m, 1.5m));
        Assert.Null(adapter.TryCreateRsiPullbackStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc.AddMinutes(5),
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            candles, 5, 14, 30m, 45m, 20, 20, 14, .2m, 1.5m));
    }

    [Fact]
    public void MacdCrossStructureFreezesThePreCrossLowAndGrossTarget()
    {
        var candles = MacdCrossEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("macd-volume-trend-acceleration-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed MACD cross.");
        var adapter = ApprovedPaperExecutionPlanCatalog.MacdCrossStructure;

        var plan = adapter.TryCreateMacdCrossStructure(proposal, candles, 5,
            12, 26, 9, 20, 1m, 14, .2m, 1.5m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value;
        Assert.NotNull(atr);
        var swingLow = candles.TakeLast(5).Min(candle => candle.Low);
        var expectedStop = swingLow - atr!.Value * .2m;
        Assert.Equal(expectedStop, plan.ProtectiveStopPrice);
        Assert.Equal(candles[^1].Close + (candles[^1].Close - expectedStop) * 1.5m,
            plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(320, plan.SourceCandles.Count);
        Assert.Contains("Frozen 5-candle crossing swing low", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateMacdCrossStructure(proposal, candles, 5,
                12, 26, 9, 20, 1m, 14, .2m, 1.5m)!.Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateMacdCrossStructure(proposal, candles, 5,
            12, 26, 9, 20, 1m, 14, .3m, 1.5m)!.ProtectiveStopPrice < expectedStop);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void MacdCrossStructureRejectsNoncrossingUnclosedAndIncorrectEvidence()
    {
        var candles = MacdCrossEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("macd-volume-trend-acceleration-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed MACD cross.");
        var adapter = ApprovedPaperExecutionPlanCatalog.MacdCrossStructure;
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history) =>
            adapter.TryCreateMacdCrossStructure(proposal, history, 5,
                12, 26, 9, 20, 1m, 14, .2m, 1.5m);

        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 27
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 318
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 310
            ? Copy(candle, symbol: "ETH/EUR") : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 198.6m, 199.1m, 198.5m, 198.6m, 100m, true, false)
            : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? Copy(candle, volume: 1m) : candle).ToArray()));
        Assert.Null(adapter.TryCreateMacdCrossStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc.AddMinutes(15),
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            candles, 5, 12, 26, 9, 20, 1m, 14, .2m, 1.5m));
        Assert.Null(adapter.TryCreateMacdCrossStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            candles, 5, 12, 26, 9, 20, 1m, 14, .2m, 1.5m));
    }

    [Fact]
    public void EmaPullbackStructureFreezesTheClosedSwingWithConfiguredRiskGeometry()
    {
        var candles = EmaPullbackEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("ema-trend-continuation-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed EMA resumption.");
        var adapter = ApprovedPaperExecutionPlanCatalog.EmaPullbackStructure;
        var plan = adapter.TryCreateEmaPullbackStructure(
            proposal, candles, 5, 20, 50, 20, 14, .2m, 1.5m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value;
        Assert.NotNull(atr);
        var expectedStop = candles.TakeLast(5).Min(candle => candle.Low) - atr!.Value * .2m;
        Assert.Equal(expectedStop, plan.ProtectiveStopPrice);
        Assert.Equal(candles[^1].Close + (candles[^1].Close - expectedStop) * 1.5m,
            plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(320, plan.SourceCandles.Count);
        Assert.Contains("Frozen 5-candle EMA pullback low", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateEmaPullbackStructure(
                proposal, candles, 5, 20, 50, 20, 14, .2m, 1.5m)!.Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateEmaPullbackStructure(
            proposal, candles, 5, 20, 50, 20, 14, .3m, 1.5m)!.ProtectiveStopPrice < expectedStop);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void EmaPullbackStructureRejectsMissingPullbackAndUnsafeEvidence()
    {
        var candles = EmaPullbackEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("ema-trend-continuation-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed EMA resumption.");
        var adapter = ApprovedPaperExecutionPlanCatalog.EmaPullbackStructure;
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history) =>
            adapter.TryCreateEmaPullbackStructure(proposal, history, 5,
                20, 50, 20, 14, .2m, 1.5m);

        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 27
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 318
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 310
            ? Copy(candle, symbol: "ETH/EUR") : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 318
            ? Copy(candle, low: candle.Close - 1m) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? Copy(candle, volume: 1m) : candle).ToArray()));
        Assert.Null(adapter.TryCreateEmaPullbackStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc.AddMinutes(15),
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            candles, 5, 20, 50, 20, 14, .2m, 1.5m));
        Assert.Null(adapter.TryCreateEmaPullbackStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            candles, 5, 20, 50, 20, 14, .2m, 1.5m));
    }

    [Fact]
    public void CompressionRangeStructureFreezesThePriorClosedRangeAndTarget()
    {
        var candles = CompressionBreakoutEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("volatility-compression-breakout-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed compression break.");
        var adapter = ApprovedPaperExecutionPlanCatalog.CompressionRangeStructure;
        var plan = adapter.TryCreateCompressionRangeStructure(
            proposal, candles, 20, 20, 1.2m, 14, 1m, .2m, 1.5m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value;
        Assert.NotNull(atr);
        var priorRange = candles.SkipLast(1).TakeLast(20).ToArray();
        var expectedStop = priorRange.Min(candle => candle.Low) - atr!.Value * .2m;
        Assert.Equal(expectedStop, plan.ProtectiveStopPrice);
        Assert.Equal(candles[^1].Close + (candles[^1].Close - expectedStop) * 1.5m,
            plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(320, plan.SourceCandles.Count);
        Assert.Contains("Frozen prior 20-candle compressed range", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateCompressionRangeStructure(
                proposal, candles, 20, 20, 1.2m, 14, 1m, .2m, 1.5m)!.Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateCompressionRangeStructure(
            proposal, candles, 20, 20, 1.2m, 14, 1m, .3m, 1.5m)!.ProtectiveStopPrice < expectedStop);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void CompressionRangeStructureRejectsBreakoutWithoutPriorSafeEvidence()
    {
        var candles = CompressionBreakoutEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("volatility-compression-breakout-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed compression break.");
        var adapter = ApprovedPaperExecutionPlanCatalog.CompressionRangeStructure;
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history) =>
            adapter.TryCreateCompressionRangeStructure(proposal, history, 20,
                20, 1.2m, 14, 1m, .2m, 1.5m);

        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 27
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 318
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 310
            ? Copy(candle, symbol: "ETH/EUR") : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 100m, 100.2m, 99.9m, 100.1m, 200m, true, false)
            : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? Copy(candle, volume: 1m) : candle).ToArray()));
        Assert.Null(adapter.TryCreateCompressionRangeStructure(
            proposal, candles, 20, 20, 1.2m, 14, .1m, .2m, 1.5m));
        Assert.Null(adapter.TryCreateCompressionRangeStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            candles, 20, 20, 1.2m, 14, 1m, .2m, 1.5m));
    }

    [Fact]
    public void MomentumDailyStructureFreezesOnlyThePreDecisionDailySwing()
    {
        var candles = MomentumDailyEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("cross-sectional-momentum-rotation-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Ranked daily universe.");
        var adapter = ApprovedPaperExecutionPlanCatalog.MomentumDailyStructure;
        var plan = adapter.TryCreateMomentumDailyStructure(proposal, candles, 10, 200, 90, 14, .2m, 1.5m);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value!.Value;
        var precedingLow = candles.SkipLast(1).TakeLast(10).Min(candle => candle.Low);
        var expectedStop = precedingLow - atr * .2m;
        Assert.Equal(expectedStop, plan.ProtectiveStopPrice);
        Assert.Equal(candles[^1].Close + (candles[^1].Close - expectedStop) * 1.5m,
            plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(320, plan.SourceCandles.Count);
        Assert.Contains("Frozen prior 10-day swing low", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId, adapter.TryCreateMomentumDailyStructure(
            proposal, candles, 10, 200, 90, 14, .2m, 1.5m)!.Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateMomentumDailyStructure(
            proposal, candles, 10, 200, 90, 14, .3m, 1.5m)!.ProtectiveStopPrice < expectedStop);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void MomentumDailyStructureRejectsMissingFutureOrUnsafeDailyEvidence()
    {
        var candles = MomentumDailyEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("cross-sectional-momentum-rotation-v1"),
            candles[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Ranked daily universe.");
        var adapter = ApprovedPaperExecutionPlanCatalog.MomentumDailyStructure;
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history) =>
            adapter.TryCreateMomentumDailyStructure(proposal, history, 10, 200, 90, 14, .2m, 1.5m);

        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 27
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 318
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 310
            ? Copy(candle, symbol: "ETH/EUR") : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? Copy(candle, interval: CandleInterval.FourHours) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? Copy(candle, low: candle.Close + 1m) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? Copy(candle, volume: 0m) : candle).ToArray()));
        Assert.Null(adapter.TryCreateMomentumDailyStructure(proposal, candles, 2, 200, 90, 14, .2m, 1.5m));
        Assert.Null(adapter.TryCreateMomentumDailyStructure(proposal, candles, 10, 200, 90, 14, .2m, 5m));
        Assert.Null(adapter.TryCreateMomentumDailyStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc.AddDays(1),
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            candles, 10, 200, 90, 14, .2m, 1.5m));
        Assert.Null(adapter.TryCreateMomentumDailyStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            candles, 10, 200, 90, 14, .2m, 1.5m));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                candle.Open, candle.High, candle.Low, 100m, candle.Volume, true, false)
            : candle).ToArray()));
    }

    [Fact]
    public void ThreeSwingStructureFreezesConfirmedPivotAndFullTimeframeEvidence()
    {
        var (signal, hourly, fourHourly) = ThreeSwingChannelDivergenceModelTests.BullishEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("three-swing-channel-divergence-v1"),
            signal[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m,
            "Confirmed closed three-swing reversal.");
        var rules = ThreeSwingDefaults();
        var adapter = ApprovedPaperExecutionPlanCatalog.ThreeSwingPivotStructure;
        var evidence = ThreeSwingChannelDivergenceModel.Evaluate(signal, hourly, fourHourly);
        Assert.Equal(ThreeSwingDivergenceDirection.Bullish, evidence.Direction);
        Assert.True(evidence.IsNearFiveMinuteChannel);
        Assert.True(evidence.IsOneHourContextAligned);
        Assert.True(evidence.HasReversalConfirmation);
        Assert.True(evidence.HasMacdConfirmation);
        var plan = adapter.TryCreateThreeSwingPivotStructure(
            proposal, signal, hourly, fourHourly, rules);

        Assert.NotNull(plan);
        Assert.Equal(3, evidence.Pivots.Count);
        var low = signal.Skip(evidence.Pivots[^1].CandleIndex).Min(candle => candle.Low);
        var atr = new AverageTrueRangeCalculator(14).Calculate(signal).Value!.Value;
        Assert.Equal(low - atr * .2m, plan.ProtectiveStopPrice);
        Assert.Equal(signal[^1].Close + (signal[^1].Close - plan.ProtectiveStopPrice) * 1.5m,
            plan.ConservativeTargetPrice);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(960, plan.SourceCandles.Count);
        Assert.Contains("Confirmed bullish pivots", plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(plan.Provenance.ResearchEvidenceId, adapter.TryCreateThreeSwingPivotStructure(
            proposal, signal, hourly, fourHourly, rules)!.Provenance.ResearchEvidenceId);
        Assert.NotEqual(plan.Provenance.ResearchEvidenceId,
            adapter.TryCreateThreeSwingPivotStructure(proposal,
                signal.Select((candle, index) => index == 0
                    ? Copy(candle, volume: candle.Volume + 1m) : candle).ToArray(),
                hourly, fourHourly, rules)!.Provenance.ResearchEvidenceId);
        Assert.True(adapter.TryCreateThreeSwingPivotStructure(
            proposal, signal, hourly, fourHourly, rules with { StopBufferAtr = .3m })!
            .ProtectiveStopPrice < plan.ProtectiveStopPrice);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, signal)!.Provenance.PlanProfileVersion);
    }

    [Fact]
    public void ThreeSwingStructureRejectsMissingUnsafeOrUnconfirmedEvidence()
    {
        var (signal, hourly, fourHourly) = ThreeSwingChannelDivergenceModelTests.BullishEvidence();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("three-swing-channel-divergence-v1"),
            signal[^1].CloseTimeUtc, StrategyAnalysisDirection.Bullish, 1m, "Closed reversal.");
        var adapter = ApprovedPaperExecutionPlanCatalog.ThreeSwingPivotStructure;
        PaperExecutionPlan? Create(IReadOnlyList<Candle> five, IReadOnlyList<Candle> hour,
            IReadOnlyList<Candle> four, ThreeSwingPaperPlanRules? rules = null) =>
            adapter.TryCreateThreeSwingPivotStructure(proposal, five, hour, four, rules ?? ThreeSwingDefaults());

        Assert.Null(Create(signal.Skip(1).ToArray(), hourly, fourHourly));
        Assert.Null(Create(signal, hourly.Skip(1).ToArray(), fourHourly));
        Assert.Null(Create(signal, hourly, fourHourly.Skip(1).ToArray()));
        Assert.Null(Create(signal.Select((candle, index) => index == 210
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray(),
            hourly, fourHourly));
        Assert.Null(Create(signal.Select((candle, index) => index == 318
            ? Copy(candle, closed: false) : candle).ToArray(), hourly, fourHourly));
        Assert.Null(Create(signal, hourly.Select((candle, index) => index == 319
            ? Copy(candle, symbol: "ETH/EUR") : candle).ToArray(), fourHourly));
        Assert.Null(Create(signal, hourly, fourHourly.Select((candle, index) => index == 319
            ? Copy(candle, closeTime: candle.CloseTimeUtc.AddHours(4)) : candle).ToArray()));
        Assert.Null(Create(signal, hourly, fourHourly, ThreeSwingDefaults() with
        {
            ContextBullishMaximumPercent = 10m
        }));
        Assert.Null(Create(signal, hourly, fourHourly, ThreeSwingDefaults() with
        {
            MinimumPriceProgressPercent = 10m
        }));
        Assert.Null(adapter.TryCreateThreeSwingPivotStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc.AddMinutes(5),
                StrategyAnalysisDirection.Bullish, 1m, proposal.Rationale),
            signal, hourly, fourHourly, ThreeSwingDefaults()));
        Assert.Null(adapter.TryCreateThreeSwingPivotStructure(
            new StrategyAnalysisProposal(proposal.TemplateId, proposal.ObservedAtUtc,
                StrategyAnalysisDirection.Bearish, 1m, proposal.Rationale),
            signal, hourly, fourHourly, ThreeSwingDefaults()));
    }

    private static ThreeSwingPaperPlanRules ThreeSwingDefaults() =>
        new(14, 12, 26, 9, 50, 2, 120, 20m, 60m, 40m, 1, 0m, 0m,
            true, true, 14, .2m, 1.5m);

    [Fact]
    public async Task EnsembleStructureFreezesItsOwnSwingAndPinsTheSelectedComponentIdentity()
    {
        var candles = (await new ContinuousPaperOpportunityScannerTests.RegimeHistorySource(Start)
            .FetchAsync("ETH/EUR", CandleInterval.FourHours, Start.AddDays(-100))).ToArray();
        var proposal = new StrategyAnalysisProposal(
            new StrategyTemplateId("regime-switching-ensemble-v1"), Start,
            StrategyAnalysisDirection.Bullish, 1m, "Pinned trend component.");
        var adapter = ApprovedPaperExecutionPlanCatalog.RegimeSelectedSwingStructure;
        var fingerprint = new string('A', 64);
        PaperExecutionPlan? Create(IReadOnlyList<Candle> history, string component = "platform.ema-trend-continuation",
            int version = 4, string? proof = null) =>
            adapter.TryCreateRegimeSelectedSwingStructure(proposal, history, component, version,
                proof ?? fingerprint, 5, 14, .2m, 1.5m);
        var plan = Create(candles);

        Assert.NotNull(plan);
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value!.Value;
        var low = candles.SkipLast(1).TakeLast(5).Min(candle => candle.Low);
        Assert.Equal(low - atr * .2m, plan.ProtectiveStopPrice);
        Assert.Equal(candles[^1].Close
            + (candles[^1].Close - plan.ProtectiveStopPrice) * 1.5m,
            plan.ConservativeTargetPrice);
        Assert.Contains("Not the selected component's own stop", plan.Rationale, StringComparison.Ordinal);
        Assert.Contains(fingerprint, plan.Rationale, StringComparison.Ordinal);
        Assert.Equal(2, plan.Provenance.PlanProfileVersion);
        Assert.Equal(320, plan.SourceCandles.Count);
        Assert.Equal(plan.Provenance.ResearchEvidenceId,
            Create(candles)!.Provenance.ResearchEvidenceId);
        Assert.NotEqual(plan.Provenance.ResearchEvidenceId,
            Create(candles, proof: new string('B', 64))!.Provenance.ResearchEvidenceId);
        Assert.Equal(1, new ApprovedPaperExecutionPlanCatalog().GetRequired(proposal.TemplateId)
            .TryCreate(proposal, candles)!.Provenance.PlanProfileVersion);
        Assert.Null(Create(candles.Skip(1).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 200
            ? Copy(candle, open: candle.OpenTimeUtc.AddMinutes(1),
                closeTime: candle.CloseTimeUtc.AddMinutes(1)) : candle).ToArray()));
        Assert.Null(Create(candles.Select((candle, index) => index == 319
            ? Copy(candle, closed: false) : candle).ToArray()));
        Assert.Null(Create(candles, version: 5));
        Assert.Null(Create(candles, proof: "not-a-fingerprint"));
        Assert.Null(Create(candles, component: "platform.session-conditioned-breakout"));
        Assert.Null(adapter.TryCreateRegimeSelectedSwingStructure(proposal, candles,
            "platform.ema-trend-continuation", 4, fingerprint, 5, 14, .2m, 5m));
    }

    [Fact]
    public void PlanEvidenceFingerprintIsIndependentOfHostCultureAndDetectsChangedCandles()
    {
        var (proposal, confirmation, setup) = RelativeEvidence();
        var adapter = ApprovedPaperExecutionPlanCatalog.RelativeStrengthStructure;
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var us = adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 5, 14, .2m, 20, 1m);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, setup, 5, 14, .2m, 20, 1m);
            Assert.Equal(us!.Provenance.ResearchEvidenceId, french!.Provenance.ResearchEvidenceId);
            var changed = setup.Select((candle, index) => index == 1
                ? Copy(candle, volume: candle.Volume + 1m) : candle).ToArray();
            Assert.NotEqual(us.Provenance.ResearchEvidenceId,
                adapter.TryCreateRelativeStrengthStructure(proposal, confirmation, changed, 5, 14, .2m, 20, 1m)!
                    .Provenance.ResearchEvidenceId);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static (StrategyAnalysisProposal Proposal, Candle[] Confirmation, Candle[] Setup) RelativeEvidence()
    {
        var setupClose = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var setup = Enumerable.Range(0, 51).Select(index =>
        {
            var open = setupClose.AddHours((index - 51) * 4);
            return new Candle("ETH/EUR", CandleInterval.FourHours, open, open.AddHours(4),
                100m, index == 40 ? 104m : 101m,
                index == 45 ? 95m : index == 50 ? 98m : 99m, 100m, 10m, true, false);
        }).ToArray();
        var confirmation = Enumerable.Range(0, 14).Select(index =>
        {
            var open = setupClose.AddHours(index - 13);
            return new Candle("ETH/EUR", CandleInterval.OneHour, open, open.AddHours(1),
                100m, 101m, 99m, 100m, 10m, true, false);
        }).ToArray();
        return (new StrategyAnalysisProposal(new StrategyTemplateId("relative-strength-pullback-rotation-v1"),
            setupClose.AddHours(1), StrategyAnalysisDirection.Bullish, 1m, "Closed confirmation."),
            confirmation, setup);
    }

    private static Candle[] DonchianEvidence() =>
        Enumerable.Range(0, 21).Select(index =>
        {
            var open = Start.AddMinutes(index * 15);
            return new Candle("BTC/USD", CandleInterval.FifteenMinutes, open, open.AddMinutes(15),
                99m, index == 20 ? 103m : 101m,
                index == 19 ? 96m : 98m,
                index == 20 ? 102m : 99m, 10m, true, false);
        }).ToArray();

    private static Candle[] BollingerEvidence() =>
        Enumerable.Range(0, 21).Select(index =>
        {
            var open = Start.AddMinutes(index * 15);
            return new Candle("BTC/USD", CandleInterval.FifteenMinutes, open, open.AddMinutes(15),
                index == 20 ? 92m : 100m, index == 20 ? 96m : 101m,
                index >= 19 ? 92m : 99m,
                index == 19 ? 92m : index == 20 ? 95.5m : 100m, 10m, true, false);
        }).ToArray();

    private static Candle[] RsiPullbackEvidence()
    {
        static decimal CloseAt(int index) =>
            index <= 304 ? 200m : index == 319 ? 196m
                : 201m - (index - 305) / 2 - ((index - 305) % 2 == 1 ? 2m : 0m);
        return Enumerable.Range(0, 320).Select(index =>
        {
            var openTime = Start.AddMinutes(index * 5);
            var open = CloseAt(Math.Max(0, index - 1));
            var close = CloseAt(index);
            return new Candle("BTC/USD", CandleInterval.FiveMinutes, openTime, openTime.AddMinutes(5),
                open, Math.Max(open, close) + .5m, Math.Min(open, close) - .5m,
                close, 100m, true, false);
        }).ToArray();
    }

    private static Candle[] MacdCrossEvidence()
    {
        static decimal CloseAt(int index) =>
            index <= 304 ? 200m : index == 319 ? 200.6m
                : 200m - (index - 304) * .1m;
        return Enumerable.Range(0, 320).Select(index =>
        {
            var openTime = Start.AddMinutes(index * 15);
            var open = CloseAt(Math.Max(0, index - 1));
            var close = CloseAt(index);
            return new Candle("BTC/USD", CandleInterval.FifteenMinutes,
                openTime, openTime.AddMinutes(15), open,
                Math.Max(open, close) + .5m, Math.Min(open, close) - .5m,
                close, 100m, true, false);
        }).ToArray();
    }

    private static Candle[] EmaPullbackEvidence() =>
        Enumerable.Range(0, 320).Select(index =>
        {
            var openTime = Start.AddMinutes(index * 5);
            var price = 100m + index;
            return new Candle("BTC/USD", CandleInterval.FiveMinutes,
                openTime, openTime.AddMinutes(5), price, price + 1m,
                price - (index == 318 ? 12m : 1m), price + .5m,
                index == 319 ? 200m : 100m, true, false);
        }).ToArray();

    private static Candle[] CompressionBreakoutEvidence() =>
        Enumerable.Range(0, 320).Select(index =>
        {
            var openTime = Start.AddMinutes(index * 15);
            var compressed = index >= 285 && index < 319;
            var close = index == 319 ? 100.3m
                : compressed ? 100m : index % 2 == 0 ? 105m : 95m;
            var open = index == 0 ? close
                : index == 319 || compressed ? 100m : index % 2 == 0 ? 95m : 105m;
            var range = index == 319 ? .1m : compressed ? .1m : .5m;
            return new Candle("BTC/USD", CandleInterval.FifteenMinutes,
                openTime, openTime.AddMinutes(15), open,
                Math.Max(open, close) + range, Math.Min(open, close) - range,
                close, index == 319 ? 200m : 100m, true, false);
        }).ToArray();

    private static Candle[] MomentumDailyEvidence() =>
        Enumerable.Range(0, 320).Select(index =>
        {
            var open = Start.AddDays(index);
            var price = 100m + index * .3m;
            var low = index == 316 ? price - 4m : price - 1m;
            return new Candle("XBT/EUR", CandleInterval.OneDay, open, open.AddDays(1),
                price, price + 1m, low, price, 20_000m, true, false);
        }).ToArray();

    private static Candle Copy(Candle candle, DateTimeOffset? open = null, DateTimeOffset? closeTime = null,
        CandleInterval? interval = null, string? symbol = null, decimal? low = null,
        decimal? volume = null, bool? closed = null) =>
        new(symbol ?? candle.Symbol, interval ?? candle.Interval, open ?? candle.OpenTimeUtc,
            closeTime ?? candle.CloseTimeUtc, candle.Open, candle.High, low ?? candle.Low,
            candle.Close, volume ?? candle.Volume, closed ?? candle.IsClosed, false);

    private static Candle[] Candles() =>
        Enumerable.Range(0, 14).Select(index =>
        {
            var open = 90m + index;
            return new Candle("BTC/USD", CandleInterval.OneHour, Start.AddHours(index), Start.AddHours(index + 1),
                open, open + 2m, open - 1m, index == 13 ? 100m : open + 1m, 10m, true, false);
        }).ToArray();
}
