using Trading.Application.Experiments;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Risk;
using Trading.Strategies;
using Microsoft.Extensions.DependencyInjection;

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
        ["platform.rsi-macd-confluence"] = "rsi-macd-confluence-v1",
        ["platform.ema-rsi-trend"] = "ema-rsi-trend-v1",
        ["platform.bollinger-macd-recovery"] = "bollinger-macd-recovery-v1",
        ["platform.donchian-volume-breakout"] = "donchian-volume-breakout-v1",
        ["platform.ema-volume-pullback"] = "ema-volume-pullback-v1",
        ["platform.cross-sectional-momentum-rotation"] = "cross-sectional-momentum-rotation-v1",
        ["platform.relative-strength-pullback-rotation"] = "relative-strength-pullback-rotation-v1",
        ["platform.session-conditioned-breakout"] = "session-conditioned-breakout-v1",
        ["platform.regime-switching-ensemble"] = "regime-switching-ensemble-v1",
        ["platform.three-swing-channel-divergence"] = "three-swing-channel-divergence-v1"
    };
    private static readonly Guid s_instrumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private readonly IServiceScopeFactory _scopes;
    private readonly ApprovedPaperExecutionPlanCatalog _plans;
    private readonly TimeProvider _time;
    private readonly decimal _estimatedTakerFeeRate;

    public DurablePaperTrainingSizingSnapshotSource(IServiceScopeFactory scopes, TimeProvider time, PaperExecutionAdapter? paperAdapter = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _plans = new ApprovedPaperExecutionPlanCatalog();
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _estimatedTakerFeeRate = paperAdapter?.EstimatedTakerFeeRate ?? PaperExecutionAdapter.DefaultEstimatedTakerFeeRate;
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
        var structuralRelative = worker.StrategyId == "platform.relative-strength-pullback-rotation"
            && evidence.StrategyVersion >= 5;
        var structuralDonchian = worker.StrategyId == "platform.donchian-breakout-ensemble"
            && evidence.StrategyVersion >= 5;
        var structuralBollinger = worker.StrategyId == "platform.bollinger-mean-reversion"
            && evidence.StrategyVersion >= 5;
        var structuralRsi = worker.StrategyId == "platform.rsi-pullback"
            && evidence.StrategyVersion >= 5;
        var structuralMacd = worker.StrategyId == "platform.macd-volume"
            && evidence.StrategyVersion >= 5;
        var structuralEma = worker.StrategyId == "platform.ema-trend-continuation"
            && evidence.StrategyVersion >= 5;
        var structuralCompression = worker.StrategyId == "platform.volatility-compression-breakout"
            && evidence.StrategyVersion >= 5;
        var structuralMomentum = worker.StrategyId == "platform.cross-sectional-momentum-rotation"
            && evidence.StrategyVersion >= 4;
        var structuralThreeSwing = worker.StrategyId == "platform.three-swing-channel-divergence"
            && evidence.StrategyVersion >= 4;
        var structuralRegime = worker.StrategyId == "platform.regime-switching-ensemble"
            && evidence.StrategyVersion >= 5;
        if ((structuralRelative || structuralDonchian || structuralBollinger || structuralRsi
                || structuralMacd || structuralEma || structuralCompression || structuralMomentum
                || structuralThreeSwing || structuralRegime)
            && (assignment.Provenance.AdmissionCloseUtc != evidence.AsOfUtc
                || !ApprovedStrategyParameters.TryNormalize(worker.StrategyId, worker.StrategyParameters,
                    out _, out _)))
            return null;
        if (structuralMomentum
            && (assignment.Provenance.RankingUniverseSymbols is not { Count: >= 2 and <= 50 } ranked
                || !ranked.Contains("XBT/EUR", StringComparer.OrdinalIgnoreCase)
                || !ranked.Contains(worker.MarketSymbol, StringComparer.OrdinalIgnoreCase)))
            return null;
        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();
        var series = await repository.ListAsync(worker.MarketSymbol, evidence.Interval, evidence.OpenTimeUtc - TimeSpan.FromTicks(duration.Ticks * 13),
            evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false);
        var candles = series.OrderBy(candle => candle.OpenTimeUtc).ThenBy(candle => candle.CloseTimeUtc).ToArray();
        if (!HasExactClosedEvidence(candles, evidence, duration))
            return null;

        var proposal = new StrategyAnalysisProposal(new StrategyTemplateId(templateId),
            evidence.AsOfUtc, StrategyAnalysisDirection.Bullish, 1m, observation.Reason);
        PaperExecutionPlan? plan;
        if (structuralDonchian)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("donchianPlanModel") != "priorBreakRange")
                return null;
            var shortChannel = parameters.Int32("shortChannel");
            var atrPeriod = parameters.Int32("atrPeriod");
            var count = Math.Max(shortChannel, atrPeriod) + 1;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, evidence.Interval,
                evidence.AsOfUtc.AddTicks(-duration.Ticks * count),
                evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.DonchianStructure.TryCreateDonchianStructure(
                proposal, history, shortChannel, atrPeriod,
                parameters.Decimal("stopBufferAtr"), parameters.Decimal("targetRiskMultiple"));
        }
        else if (structuralBollinger)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("bollingerPlanModel") != "excursionMidBand")
                return null;
            var bandPeriod = parameters.Int32("bollingerPeriod");
            var atrPeriod = parameters.Int32("atrPeriod");
            var count = Math.Max(bandPeriod, atrPeriod) + 1;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, evidence.Interval,
                evidence.AsOfUtc.AddTicks(-duration.Ticks * count),
                evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.BollingerStructure.TryCreateBollingerStructure(
                proposal, history, bandPeriod, parameters.Decimal("bollingerDeviation"),
                atrPeriod, parameters.Decimal("stopBufferAtr"),
                parameters.Decimal("minimumRewardRisk"));
        }
        else if (structuralRsi)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("rsiPlanModel") != "pullbackSwing")
                return null;
            var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, evidence.Interval,
                evidence.AsOfUtc.AddTicks(-duration.Ticks * count),
                evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.RsiPullbackStructure.TryCreateRsiPullbackStructure(
                proposal, history, parameters.Int32("stopSwingLookback"),
                parameters.Int32("rsiPeriod"),
                parameters.Decimal("longRsiMinimum"), parameters.Decimal("longRsiMaximum"),
                parameters.Int32("signalEma"), parameters.Int32("volumePeriod"),
                parameters.Int32("atrPeriod"), parameters.Decimal("stopBufferAtr"),
                parameters.Decimal("targetRiskMultiple"));
        }
        else if (structuralMacd)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("macdPlanModel") != "crossSwing")
                return null;
            var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, evidence.Interval,
                evidence.AsOfUtc.AddTicks(-duration.Ticks * count),
                evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.MacdCrossStructure.TryCreateMacdCrossStructure(
                proposal, history, parameters.Int32("stopSwingLookback"),
                parameters.Int32("macdFast"), parameters.Int32("macdSlow"),
                parameters.Int32("macdSignal"), parameters.Int32("volumePeriod"),
                parameters.Decimal("volumeMultiplier"),
                parameters.Int32("atrPeriod"), parameters.Decimal("stopBufferAtr"),
                parameters.Decimal("targetRiskMultiple"));
        }
        else if (structuralEma)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("emaPlanModel") != "pullbackSwing")
                return null;
            var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, evidence.Interval,
                evidence.AsOfUtc.AddTicks(-duration.Ticks * count),
                evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.EmaPullbackStructure.TryCreateEmaPullbackStructure(
                proposal, history, parameters.Int32("stopSwingLookback"),
                parameters.Int32("signalPullbackEma"), parameters.Int32("signalInvalidationEma"),
                parameters.Int32("volumePeriod"), parameters.Int32("atrPeriod"),
                parameters.Decimal("stopBufferAtr"), parameters.Decimal("targetRiskMultiple"));
        }
        else if (structuralCompression)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("compressionPlanModel") != "priorRange")
                return null;
            var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, evidence.Interval,
                evidence.AsOfUtc.AddTicks(-duration.Ticks * count),
                evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.CompressionRangeStructure.TryCreateCompressionRangeStructure(
                proposal, history, parameters.Int32("channelPeriod"),
                parameters.Int32("volumePeriod"), parameters.Decimal("volumeMultiplier"),
                parameters.Int32("atrPeriod"), parameters.Decimal("maximumExtensionAtr"),
                parameters.Decimal("stopBufferAtr"), parameters.Decimal("targetRiskMultiple"));
        }
        else if (structuralMomentum)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("momentumPlanModel") != "dailySwing")
                return null;
            var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, CandleInterval.OneDay,
                evidence.AsOfUtc.AddDays(-count),
                evidence.OpenTimeUtc, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.MomentumDailyStructure.TryCreateMomentumDailyStructure(
                proposal, history, parameters.Int32("stopSwingLookback"),
                parameters.Int32("trendEma"), parameters.Int32("trendRankLookback"),
                parameters.Int32("atrPeriod"), parameters.Decimal("stopBufferAtr"),
                parameters.Decimal("targetRiskMultiple"));
        }
        else if (structuralThreeSwing)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("threeSwingPlanModel") != "confirmedPivot")
                return null;
            var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
            var signal = (await repository.ListAsync(worker.MarketSymbol, CandleInterval.FiveMinutes,
                evidence.AsOfUtc.AddMinutes(-5 * count), evidence.OpenTimeUtc,
                cancellationToken).ConfigureAwait(false)).OrderBy(candle => candle.OpenTimeUtc).ToArray();
            var hourClose = new DateTimeOffset(
                evidence.AsOfUtc.UtcTicks - evidence.AsOfUtc.UtcTicks % TimeSpan.TicksPerHour,
                TimeSpan.Zero);
            var hours = (await repository.ListAsync(worker.MarketSymbol, CandleInterval.OneHour,
                hourClose.AddHours(-count), hourClose.AddHours(-1),
                cancellationToken).ConfigureAwait(false)).OrderBy(candle => candle.OpenTimeUtc).ToArray();
            var fourHourTicks = TimeSpan.FromHours(4).Ticks;
            var fourHourClose = new DateTimeOffset(
                evidence.AsOfUtc.UtcTicks - evidence.AsOfUtc.UtcTicks % fourHourTicks, TimeSpan.Zero);
            var fourHours = (await repository.ListAsync(worker.MarketSymbol, CandleInterval.FourHours,
                fourHourClose.AddTicks(-fourHourTicks * count), fourHourClose.AddTicks(-fourHourTicks),
                cancellationToken).ConfigureAwait(false)).OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (signal.Length != count || hours.Length != count || fourHours.Length != count
                || signal[^1].CloseTimeUtc != evidence.AsOfUtc
                || hours[^1].CloseTimeUtc != hourClose
                || fourHours[^1].CloseTimeUtc != fourHourClose)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.ThreeSwingPivotStructure
                .TryCreateThreeSwingPivotStructure(proposal, signal, hours, fourHours,
                    new ThreeSwingPaperPlanRules(
                        parameters.Int32("rsiPeriod"), parameters.Int32("macdFast"),
                        parameters.Int32("macdSlow"), parameters.Int32("macdSignal"),
                        parameters.Int32("channelPeriod"), parameters.Int32("pivotSideBars"),
                        parameters.Int32("maximumPivotLookback"),
                        parameters.Decimal("channelProximityPercent"),
                        parameters.Decimal("contextBearishMinimumPercent"),
                        parameters.Decimal("contextBullishMaximumPercent"),
                        parameters.Int32("minimumAlignedContextTimeframes"),
                        parameters.Decimal("minimumRsiDivergencePoints"),
                        parameters.Decimal("minimumPriceProgressPercent"),
                        parameters.Boolean("requireClosedReversal"),
                        parameters.Boolean("requireMacdConfirmation"),
                        parameters.Int32("atrPeriod"), parameters.Decimal("stopBufferAtr"),
                        parameters.Decimal("targetRiskMultiple")));
        }
        else if (structuralRegime)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            var selected = assignment.Provenance.SelectedComponent;
            var confirmed = observation.SelectedComponent;
            if (parameters.String("regimePlanModel") != "fourHourSwing"
                || evidence.Interval != CandleInterval.FourHours
                || selected is null || confirmed is null
                || selected.SignalInterval != CandleInterval.FourHours
                || selected.SignalAsOfUtc != evidence.AsOfUtc
                || selected.FamilyId != confirmed.FamilyId || selected.Version != confirmed.Version
                || selected.ComponentDecisionFingerprint != confirmed.ComponentDecisionFingerprint
                || !selected.UniverseSymbols.SequenceEqual(confirmed.UniverseSymbols,
                    StringComparer.OrdinalIgnoreCase))
                return null;
            var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
            var history = (await repository.ListAsync(
                worker.MarketSymbol, evidence.Interval,
                evidence.AsOfUtc.AddHours(-4 * count), evidence.OpenTimeUtc,
                cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (history.Length != count || history[^1].CloseTimeUtc != evidence.AsOfUtc)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.RegimeSelectedSwingStructure
                .TryCreateRegimeSelectedSwingStructure(proposal, history, selected.FamilyId,
                    selected.Version, selected.ComponentDecisionFingerprint,
                    parameters.Int32("stopSwingLookback"), parameters.Int32("planAtrPeriod"),
                    parameters.Decimal("stopBufferAtr"), parameters.Decimal("targetRiskMultiple"));
        }
        else if (structuralRelative)
        {
            var parameters = ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters);
            if (parameters.String("relativePlanModel") != "fourHourStructure")
                return null;
            var fourHours = TimeSpan.FromHours(4);
            var setupClose = new DateTimeOffset(
                evidence.AsOfUtc.UtcTicks - evidence.AsOfUtc.UtcTicks % fourHours.Ticks, TimeSpan.Zero);
            var setup = (await repository.ListAsync(
                worker.MarketSymbol, CandleInterval.FourHours,
                setupClose.AddTicks(-fourHours.Ticks * 51),
                setupClose - fourHours, cancellationToken).ConfigureAwait(false))
                .OrderBy(candle => candle.OpenTimeUtc).ToArray();
            if (setup.Length != 51 || setup[^1].CloseTimeUtc != setupClose)
                return null;
            plan = ApprovedPaperExecutionPlanCatalog.RelativeStrengthStructure.TryCreateRelativeStrengthStructure(
                proposal, candles, setup,
                parameters.Int32("stopSwingLookback"), parameters.Int32("atrPeriod"),
                parameters.Decimal("stopBufferAtr"), parameters.Int32("targetChannelLookback"),
                parameters.Decimal("minimumRewardRisk"));
        }
        else
            plan = _plans.GetRequired(new StrategyTemplateId(templateId)).TryCreate(proposal, candles);
        if (plan is null)
            return null;

        var now = _time.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || now < evidence.AsOfUtc)
            return null;
        var nextMinute = (await repository.ListAsync(
            worker.MarketSymbol, CandleInterval.OneMinute,
            evidence.CloseTimeUtc, evidence.CloseTimeUtc, cancellationToken).ConfigureAwait(false)).ToArray();
        if (nextMinute.Length != 1)
            return null;
        var execution = nextMinute[0];
        if (execution.Interval != CandleInterval.OneMinute
            || !string.Equals(execution.Symbol, worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
            || !execution.CanBeUsedForClosedCandleSignal
            || execution.QualityFlags.Count != 0
            || execution.OpenTimeUtc != evidence.CloseTimeUtc
            || execution.CloseTimeUtc != evidence.CloseTimeUtc.AddMinutes(1)
            || now < execution.CloseTimeUtc || now - execution.CloseTimeUtc > TimeSpan.FromMinutes(1)
            || execution.Open <= 0m || execution.Low <= 0m
            || execution.Low > execution.Open || execution.Low > execution.Close
            || execution.High < execution.Open || execution.High < execution.Close
            || execution.Close <= plan.ProtectiveStopPrice
            || execution.Low <= plan.ProtectiveStopPrice
            || plan.ConservativeTargetPrice is decimal reachedTarget && execution.High >= reachedTarget)
            return null;
        var entryPrice = execution.Close;
        if (plan.ConservativeTargetPrice is decimal target)
        {
            if (target <= entryPrice)
                return null;
            var minimumRewardRisk = structuralRelative
                ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                    .Decimal("minimumRewardRisk")
                : structuralBollinger
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralRsi
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralMacd
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralEma
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralCompression
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralMomentum
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralThreeSwing
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralRegime
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("minimumRewardRisk")
                : structuralDonchian
                    ? ApprovedStrategyParameters.Read(worker.StrategyId, worker.StrategyParameters)
                        .Decimal("targetRiskMultiple")
                    : 0m;
            if (minimumRewardRisk > 0m
                && (target - entryPrice) / (entryPrice - plan.ProtectiveStopPrice) < minimumRewardRisk)
                return null;
        }
        var pairFilters = assignment.Provenance.PairFilters;
        if (pairFilters is null || pairFilters.PriceTick <= 0m
            || pairFilters.QuantityStep <= 0m || pairFilters.MinimumQuantity <= 0m
            || pairFilters.MinimumNotional <= 0m
            || entryPrice % pairFilters.PriceTick != 0m)
            return null;
        var isAddition = worker.PositionQuantity > 0m;
        var favorableAddApproved = isAddition && entryPrice > worker.AverageEntryPrice;
        var exposure = worker.PositionQuantity * entryPrice;
        var sizing = new PaperRiskSizingInput(worker.CashBalance + exposure, worker.CashBalance, entryPrice, plan.ProtectiveStopPrice,
            PaperPositionDirection.Long, worker.PositionQuantity, exposure, worker.PositionQuantity > 0m ? PaperPositionDirection.Long : null,
            favorableAddApproved, pairFilters,
            CreateWorkerBudget(worker),
            CreatePlatformPolicy(worker),
            execution.CloseTimeUtc, now, now, _estimatedTakerFeeRate);
        var sized = PaperRiskPositionSizer.Size(sizing);
        if (!sized.IsAccepted)
            return null;

        var eligibility = CreateEligibility(evidence.Interval, now);
        var fill = new ExperimentPaperFillRequest(worker.MarketSymbol, TradeDirection.Buy, sized.Quantity, entryPrice, false, 1m, execution.CloseTimeUtc);
        var risk = new ExperimentWorkerRiskEvaluationRequest(worker,
            new(worker.UserId, worker.Id, assignment.Group, configuration.Version, worker.StrategyId, true),
            new(worker.PositionControls.MaxPositionQuantity, Math.Min(100m, worker.PositionControls.MaxPositionNotional),
                worker.PositionControls.MaxAdditionsPerPosition),
            new(worker.UserId, worker.Id, worker.PositionQuantity, exposure, worker.AdditionCount),
            s_instrumentId, eligibility, new(EligibilityPurpose.Paper, evidence.Interval, TradingProductType.Spot), TimeSpan.FromMinutes(5),
            new StalenessPolicy(TimeSpan.FromMinutes(5)), execution.CloseTimeUtc, now, now, new TradingModeFlags(), null,
            false, false, false, false,
            new RiskLimitHierarchy(Math.Min(100m, worker.PositionControls.MaxPositionNotional),
                worker.PositionControls.MaxPositionQuantity),
            isAddition ? ExperimentProposalAction.Add : ExperimentProposalAction.Open, fill);
        var context = new ExperimentPaperWorkerContext(worker, new(worker.UserId, worker.Id, worker.PositionQuantity, execution.CloseTimeUtc),
            new(worker.MarketSymbol, evidence.Interval, evidence.OpenTimeUtc, evidence.CloseTimeUtc, evidence.AsOfUtc, candles[^1].Close, candles[^1].Volume,
                candles[^1].QualityFlags.Select(flag => flag.ToString()).ToArray()),
            new(worker.MarketSymbol, CandleInterval.OneMinute, execution.OpenTimeUtc, execution.CloseTimeUtc,
                execution.CloseTimeUtc, entryPrice, execution.Volume,
                execution.QualityFlags.Select(flag => flag.ToString()).ToArray()));
        return new(plan, context, sizing, risk);
    }

    internal static PaperWorkerSizingBudget CreateWorkerBudget(ExperimentWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return new(
            0.0025m,
            100m,
            Math.Min(100m, worker.PositionControls.MaxPositionNotional),
            worker.PositionControls.MaxPositionQuantity);
    }

    internal static PaperRiskSizingPolicy CreatePlatformPolicy(ExperimentWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return new(
            0.0025m,
            0.0025m,
            100m,
            Math.Min(100m, worker.PositionControls.MaxPositionNotional),
            worker.PositionControls.MaxPositionQuantity,
            TimeSpan.FromMinutes(5));
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
