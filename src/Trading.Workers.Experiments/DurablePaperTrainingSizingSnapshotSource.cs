using Trading.Application.Experiments;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Risk;
using Trading.Strategies;

namespace Trading.Workers.Experiments;

/// <summary>
/// Builds the one bounded paper-training input bundle from re-read durable candles. It has no
/// exchange client, credentials, live adapter, or configurable strategy-to-plan mapping.
/// </summary>
public sealed class DurablePaperTrainingSizingSnapshotSource : IPaperTrainingSizingSnapshotSource
{
    private static readonly Dictionary<string, string> s_planTemplates = new(StringComparer.Ordinal)
    {
        ["platform.ema-trend-continuation"] = "ema-trend-continuation-v1",
        ["platform.donchian-breakout-ensemble"] = "donchian-breakout-ensemble-v1",
        ["platform.bollinger-mean-reversion"] = "bollinger-mean-reversion-v1",
        ["platform.rsi-pullback"] = "rsi-pullback-v1",
        ["platform.macd-volume"] = "macd-volume-trend-acceleration-v1",
        ["platform.volatility-compression-breakout"] = "volatility-compression-breakout-v1",
        ["platform.cross-sectional-momentum-rotation"] = "cross-sectional-momentum-rotation-v1",
        ["platform.relative-strength-pullback-rotation"] = "relative-strength-pullback-rotation-v1",
        ["platform.session-conditioned-breakout"] = "session-conditioned-breakout-v1",
        ["platform.regime-switching-ensemble"] = "regime-switching-ensemble-v1"
    };
    private static readonly Guid s_instrumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly ICandleRepository _candles;
    private readonly ApprovedPaperExecutionPlanCatalog _plans;
    private readonly TimeProvider _time;

    public DurablePaperTrainingSizingSnapshotSource(ICandleRepository candles, TimeProvider time)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _plans = new ApprovedPaperExecutionPlanCatalog();
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<PaperTrainingSizingSnapshot?> GetAsync(ExperimentWorker worker, ExperimentResearchGroupConfiguration configuration,
        ExperimentResearchGroupAssignment assignment, ExperimentAnalysisResult observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(observation);
        var evidence = observation.Evidence;
        if (evidence is null || !s_planTemplates.TryGetValue(worker.StrategyId, out var templateId)
            || evidence.UserId != worker.UserId || evidence.WorkerId != worker.Id || evidence.Group != assignment.Group
            || evidence.GroupConfigurationVersion != configuration.Version || !string.Equals(evidence.Symbol, worker.MarketSymbol, StringComparison.OrdinalIgnoreCase))
            return null;

        var duration = evidence.CloseTimeUtc - evidence.OpenTimeUtc;
        if (duration <= TimeSpan.Zero)
            return null;
        var series = await _candles.ListAsync(worker.MarketSymbol, evidence.Interval, evidence.OpenTimeUtc - TimeSpan.FromTicks(duration.Ticks * 13),
            evidence.CloseTimeUtc, cancellationToken).ConfigureAwait(false);
        var candles = series.OrderBy(candle => candle.OpenTimeUtc).ThenBy(candle => candle.CloseTimeUtc).ToArray();
        if (!HasExactClosedEvidence(candles, evidence, duration))
            return null;

        var plan = _plans.GetRequired(new StrategyTemplateId(templateId)).TryCreate(
            new StrategyAnalysisProposal(new StrategyTemplateId(templateId), evidence.AsOfUtc, StrategyAnalysisDirection.Bullish, 1m, observation.Reason), candles);
        if (plan is null)
            return null;

        var now = _time.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || now < evidence.AsOfUtc)
            return null;
        var exposure = worker.PositionQuantity * candles[^1].Close;
        var sizing = new PaperRiskSizingInput(worker.CashBalance + exposure, worker.CashBalance, plan.EntryReferencePrice, plan.ProtectiveStopPrice,
            PaperPositionDirection.Long, worker.PositionQuantity, exposure, worker.PositionQuantity > 0m ? PaperPositionDirection.Long : null,
            worker.PriorFavorableMarkPrice is decimal favorableMark && favorableMark > worker.AverageEntryPrice, new PaperExchangeFilters(0.1m, 0.00000001m, 0.00000001m, 10m),
            new PaperWorkerSizingBudget(0.0025m, 100m, 100m, 1m),
            new PaperRiskSizingPolicy(0.0025m, 0.0025m, 100m, 100m, 1m, TimeSpan.FromMinutes(5)),
            evidence.AsOfUtc, evidence.AsOfUtc, now);
        var sized = PaperRiskPositionSizer.Size(sizing);
        if (!sized.IsAccepted)
            return null;

        var eligibility = CreateEligibility(evidence.Interval, now);
        var fill = new ExperimentPaperFillRequest(worker.MarketSymbol, TradeDirection.Buy, sized.Quantity, plan.EntryReferencePrice, false, 1m, evidence.AsOfUtc);
        var risk = new ExperimentWorkerRiskEvaluationRequest(worker,
            new(worker.UserId, worker.Id, assignment.Group, configuration.Version, worker.StrategyId, true),
            new(1m, 100m, worker.PositionControls.MaxAdditionsPerPosition),
            new(worker.UserId, worker.Id, worker.PositionQuantity, exposure, worker.AdditionCount),
            s_instrumentId, eligibility, new(EligibilityPurpose.Paper, evidence.Interval, TradingProductType.Spot), TimeSpan.FromMinutes(5),
            new StalenessPolicy(TimeSpan.FromMinutes(5)), evidence.AsOfUtc, evidence.AsOfUtc, now, new TradingModeFlags(), null,
            false, false, false, false, new RiskLimitHierarchy(100m, 1m), ExperimentProposalAction.Open, fill);
        var context = new ExperimentPaperWorkerContext(worker, new(worker.UserId, worker.Id, worker.PositionQuantity, evidence.AsOfUtc),
            new(worker.MarketSymbol, evidence.Interval, evidence.OpenTimeUtc, evidence.CloseTimeUtc, evidence.AsOfUtc, candles[^1].Close, candles[^1].Volume,
                candles[^1].QualityFlags.Select(flag => flag.ToString()).ToArray()));
        return new(plan, context, sizing, risk);
    }

    private static bool HasExactClosedEvidence(Candle[] candles, ExperimentDecisionEvidence evidence, TimeSpan duration) =>
        candles.Length == 14 && candles.All(candle => candle.IsClosed && candle.CanBeUsedForClosedCandleSignal
            && candle.Interval == evidence.Interval && string.Equals(candle.Symbol, evidence.Symbol, StringComparison.OrdinalIgnoreCase)
            && candle.CloseTimeUtc <= evidence.AsOfUtc)
        && candles.Select((candle, index) => candle.OpenTimeUtc == evidence.OpenTimeUtc - TimeSpan.FromTicks(duration.Ticks * (13 - index))
            && candle.CloseTimeUtc == candle.OpenTimeUtc + duration).All(value => value)
        && candles[^1].OpenTimeUtc == evidence.OpenTimeUtc && candles[^1].CloseTimeUtc == evidence.CloseTimeUtc
        && candles[^1].CloseTimeUtc == evidence.AsOfUtc;

    private static InstrumentEligibility CreateEligibility(CandleInterval interval, DateTimeOffset now)
    {
        var result = new InstrumentEligibility(s_instrumentId);
        foreach (var purpose in new[] { EligibilityPurpose.Research, EligibilityPurpose.Backtest, EligibilityPurpose.Paper })
            result.Grant(new(purpose, interval, TradingProductType.Spot), now, now, TimeSpan.FromMinutes(5));
        return result;
    }
}
