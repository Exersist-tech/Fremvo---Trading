using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;

namespace Trading.Strategies;

/// <summary>
/// A non-executable paper-analysis artifact. It deliberately has no quantity,
/// account, intent, command, exchange, or order fields.
/// </summary>
public sealed class PaperExecutionPlan
{
    internal PaperExecutionPlan(
        StrategyTemplateId templateId,
        StrategyAnalysisDirection direction,
        decimal entryReferencePrice,
        decimal protectiveStopPrice,
        decimal? conservativeTargetPrice,
        DateTimeOffset asOfUtc,
        PaperExecutionPlanProvenance provenance,
        IEnumerable<PaperExecutionPlanCandleIdentity> sourceCandles,
        string rationale)
    {
        TemplateId = templateId ?? throw new ArgumentNullException(nameof(templateId));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (direction == StrategyAnalysisDirection.Neutral)
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Plan as-of must be explicit UTC.", nameof(asOfUtc));
        if (entryReferencePrice <= 0m || protectiveStopPrice <= 0m
            || (direction == StrategyAnalysisDirection.Bullish && protectiveStopPrice >= entryReferencePrice)
            || (direction == StrategyAnalysisDirection.Bearish && protectiveStopPrice <= entryReferencePrice))
            throw new ArgumentException("A plan requires a protective stop.");
        if (conservativeTargetPrice is <= 0m
            || (conservativeTargetPrice is not null && direction == StrategyAnalysisDirection.Bullish && conservativeTargetPrice <= entryReferencePrice)
            || (conservativeTargetPrice is not null && direction == StrategyAnalysisDirection.Bearish && conservativeTargetPrice >= entryReferencePrice))
            throw new ArgumentException("A target must be favorable to the plan direction.", nameof(conservativeTargetPrice));
        if (string.IsNullOrWhiteSpace(rationale))
            throw new ArgumentException("Rationale is required.", nameof(rationale));

        var copied = sourceCandles?.ToArray() ?? throw new ArgumentNullException(nameof(sourceCandles));
        if (copied.Length == 0 || copied.Any(identity => identity is null))
            throw new ArgumentException("At least one source candle identity is required.", nameof(sourceCandles));

        Direction = direction;
        EntryReferencePrice = entryReferencePrice;
        ProtectiveStopPrice = protectiveStopPrice;
        ConservativeTargetPrice = conservativeTargetPrice;
        AsOfUtc = asOfUtc;
        SourceCandles = new ReadOnlyCollection<PaperExecutionPlanCandleIdentity>(copied);
        Rationale = rationale.Trim();
    }

    public StrategyTemplateId TemplateId { get; }
    public StrategyAnalysisDirection Direction { get; }
    public decimal EntryReferencePrice { get; }
    public decimal ProtectiveStopPrice { get; }
    public decimal? ConservativeTargetPrice { get; }
    public DateTimeOffset AsOfUtc { get; }
    public PaperExecutionPlanProvenance Provenance { get; }
    public IReadOnlyList<PaperExecutionPlanCandleIdentity> SourceCandles { get; }
    public string Rationale { get; }
}

public sealed record PaperExecutionPlanCandleIdentity(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset CloseTimeUtc);

public sealed record PaperExecutionPlanProvenance(
    string PlanProfileId,
    int PlanProfileVersion,
    string ResearchEvidenceId,
    string StrategyPlanProfileIdentity);

/// <summary>Fixed platform-authored risk geometry; this is not a user parameter surface.</summary>
public sealed class ApprovedPaperExecutionPlanProfile
{
    internal ApprovedPaperExecutionPlanProfile(StrategyTemplateId templateId, string identity, int version = 1)
    {
        TemplateId = templateId;
        Identity = identity;
        Version = version;
        AtrPeriod = 14;
        StopAtrMultiplier = 2m;
        TargetAtrMultiplier = 1.5m;
        MaximumApprovedRiskFraction = 0.0025m;
    }

    public StrategyTemplateId TemplateId { get; }
    public string Identity { get; }
    public int Version { get; }
    public int AtrPeriod { get; }
    public decimal StopAtrMultiplier { get; }
    public decimal? TargetAtrMultiplier { get; }
    public decimal MaximumApprovedRiskFraction { get; }
}

/// <summary>
/// Registered, fixed adapters for the approved families. They only turn a
/// completed research observation plus safe closed candle evidence into an inert plan.
/// </summary>
public sealed class ApprovedPaperExecutionPlanCatalog
{
    private static readonly IReadOnlyDictionary<StrategyTemplateId, ApprovedPaperExecutionPlanProfile> s_profiles =
        new ReadOnlyDictionary<StrategyTemplateId, ApprovedPaperExecutionPlanProfile>(
            new[]
            {
                "ema-trend-continuation-v1", "donchian-breakout-ensemble-v1", "bollinger-mean-reversion-v1",
                "rsi-pullback-v1", "macd-volume-trend-acceleration-v1", "volatility-compression-breakout-v1",
                "rsi-macd-confluence-v1", "ema-rsi-trend-v1", "bollinger-macd-recovery-v1",
                "donchian-volume-breakout-v1", "ema-volume-pullback-v1",
                "cross-sectional-momentum-rotation-v1", "relative-strength-pullback-rotation-v1",
                "session-conditioned-breakout-v1", "regime-switching-ensemble-v1",
                "three-swing-channel-divergence-v1"
            }.Select(id => new ApprovedPaperExecutionPlanProfile(new StrategyTemplateId(id), $"platform.paper-plan/{id}@1"))
             .ToDictionary(profile => profile.TemplateId));
    private readonly IReadOnlyDictionary<StrategyTemplateId, ApprovedPaperExecutionPlanProfile> _profiles = s_profiles;

    public IReadOnlyCollection<StrategyTemplateId> TemplateIds => _profiles.Keys.ToArray();

    public ApprovedPaperExecutionPlanAdapter GetRequired(StrategyTemplateId templateId) =>
        new(_profiles.TryGetValue(templateId ?? throw new ArgumentNullException(nameof(templateId)), out var profile)
            ? profile
            : throw new KeyNotFoundException($"No approved paper plan adapter is registered for '{templateId}'."));

    public static ApprovedPaperExecutionPlanAdapter RelativeStrengthStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("relative-strength-pullback-rotation-v1"),
            "platform.paper-plan/relative-strength-pullback-rotation-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter DonchianStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("donchian-breakout-ensemble-v1"),
            "platform.paper-plan/donchian-breakout-ensemble-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter BollingerStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("bollinger-mean-reversion-v1"),
            "platform.paper-plan/bollinger-mean-reversion-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter RsiPullbackStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("rsi-pullback-v1"),
            "platform.paper-plan/rsi-pullback-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter MacdCrossStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("macd-volume-trend-acceleration-v1"),
            "platform.paper-plan/macd-volume-trend-acceleration-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter EmaPullbackStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("ema-trend-continuation-v1"),
            "platform.paper-plan/ema-trend-continuation-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter CompressionRangeStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("volatility-compression-breakout-v1"),
            "platform.paper-plan/volatility-compression-breakout-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter MomentumDailyStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("cross-sectional-momentum-rotation-v1"),
            "platform.paper-plan/cross-sectional-momentum-rotation-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter ThreeSwingPivotStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("three-swing-channel-divergence-v1"),
            "platform.paper-plan/three-swing-channel-divergence-v1@2", 2));

    public static ApprovedPaperExecutionPlanAdapter RegimeSelectedSwingStructure =>
        new(new ApprovedPaperExecutionPlanProfile(
            new StrategyTemplateId("regime-switching-ensemble-v1"),
            "platform.paper-plan/regime-switching-ensemble-v1@2", 2));
}

public sealed record ThreeSwingPaperPlanRules(
    int RsiPeriod,
    int MacdFast,
    int MacdSlow,
    int MacdSignal,
    int ChannelPeriod,
    int PivotSideBars,
    int MaximumPivotLookback,
    decimal ChannelProximityPercent,
    decimal ContextBearishMinimumPercent,
    decimal ContextBullishMaximumPercent,
    int MinimumAlignedContextTimeframes,
    decimal MinimumRsiDivergencePoints,
    decimal MinimumPriceProgressPercent,
    bool RequireClosedReversal,
    bool RequireMacdConfirmation,
    int AtrPeriod,
    decimal StopBufferAtr,
    decimal TargetRiskMultiple);

public sealed class ApprovedPaperExecutionPlanAdapter
{
    private readonly ApprovedPaperExecutionPlanProfile _profile;

    internal ApprovedPaperExecutionPlanAdapter(ApprovedPaperExecutionPlanProfile profile) =>
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    public ApprovedPaperExecutionPlanProfile Profile => _profile;

    public PaperExecutionPlan? TryCreate(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> sourceCandles)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(sourceCandles);
        if (!observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction == StrategyAnalysisDirection.Neutral
            || observation.Confidence <= 0m
            || !HasSafeClosedEvidence(sourceCandles, observation.ObservedAtUtc))
            return null;

        var atr = new AverageTrueRangeCalculator(_profile.AtrPeriod).Calculate(sourceCandles).Value;
        if (atr is null || atr <= 0m)
            return null;

        var entry = sourceCandles[^1].Close;
        if (entry <= 0m)
            return null;

        var stop = observation.Direction == StrategyAnalysisDirection.Bullish
            ? entry - (atr.Value * _profile.StopAtrMultiplier)
            : entry + (atr.Value * _profile.StopAtrMultiplier);
        if (stop <= 0m)
            return null;

        decimal? target = _profile.TargetAtrMultiplier is decimal targetMultiplier
            ? observation.Direction == StrategyAnalysisDirection.Bullish
                ? entry + (atr.Value * targetMultiplier)
                : entry - (atr.Value * targetMultiplier)
            : null;
        if (target <= 0m)
            target = null;

        return new PaperExecutionPlan(
            _profile.TemplateId,
            observation.Direction,
            entry,
            stop,
            target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(
                _profile.Identity,
                _profile.Version,
                EvidenceIdentity(sourceCandles),
                _profile.Identity),
            sourceCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            $"{observation.Rationale} Paper plan only; no sizing, order, intent, or execution is created.");
    }

    public PaperExecutionPlan? TryCreateRelativeStrengthStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> confirmationCandles,
        IReadOnlyList<Candle> setupCandles,
        int stopSwingLookback,
        int atrPeriod,
        decimal stopBufferAtr,
        int targetChannelLookback,
        decimal minimumRewardRisk)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(confirmationCandles);
        ArgumentNullException.ThrowIfNull(setupCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || stopSwingLookback is < 3 or > 20 || atrPeriod is < 5 or > 50
            || targetChannelLookback is < 5 or > 40 || targetChannelLookback < stopSwingLookback
            || stopBufferAtr is < .1m or > 2m || minimumRewardRisk is < .5m or > 2m
            || confirmationCandles.Count != 14
            || !HasSafeClosedEvidence(confirmationCandles, observation.ObservedAtUtc)
            || confirmationCandles.Any(candle => candle.Interval != CandleInterval.OneHour
                || candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromHours(1))
            || confirmationCandles.Zip(confirmationCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || setupCandles.Count < Math.Max(atrPeriod + 1, targetChannelLookback)
            || !HasSafeClosedEvidence(setupCandles, setupCandles[^1].CloseTimeUtc)
            || setupCandles.Any(candle => candle.Interval != CandleInterval.FourHours
                || candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromHours(4)
                || !candle.Symbol.Equals(confirmationCandles[^1].Symbol, StringComparison.OrdinalIgnoreCase))
            || setupCandles.Zip(setupCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap))
            return null;

        var setupClose = setupCandles[^1].CloseTimeUtc;
        var fourHours = TimeSpan.FromHours(4);
        var expectedSetupClose = new DateTimeOffset(
            observation.ObservedAtUtc.UtcTicks - observation.ObservedAtUtc.UtcTicks % fourHours.Ticks,
            TimeSpan.Zero);
        if (setupClose != expectedSetupClose || setupClose >= observation.ObservedAtUtc)
            return null;
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(setupCandles).Value;
        if (atr is not > 0m)
            return null;

        var entry = confirmationCandles[^1].Close;
        decimal stop;
        decimal target;
        decimal risk;
        try
        {
            stop = setupCandles.TakeLast(stopSwingLookback).Min(candle => candle.Low)
                - atr.Value * stopBufferAtr;
            target = setupCandles.TakeLast(targetChannelLookback).Max(candle => candle.High);
            risk = entry - stop;
            if (stop <= 0m || risk <= 0m || target <= entry
                || (target - entry) / risk < minimumRewardRisk)
                return null;
        }
        catch (OverflowException)
        {
            return null;
        }

        var evidence = confirmationCandles.Concat(setupCandles).ToArray();
        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, entry, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(evidence), _profile.Identity),
            evidence.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Four-hour swing {stopSwingLookback}, ATR {atrPeriod} buffer {stopBufferAtr}, prior-channel target {target}; estimated gross reward/risk {(target - entry) / risk:F2}. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateDonchianStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        int shortChannel,
        int atrPeriod,
        decimal stopBufferAtr,
        decimal targetRiskMultiple)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || shortChannel is < 5 or > 60 || atrPeriod is < 5 or > 50
            || stopBufferAtr is < .1m or > 2m || targetRiskMultiple is < 1m or > 4m
            || signalCandles.Count != Math.Max(shortChannel, atrPeriod) + 1
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || signalCandles.Zip(signalCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || signalCandles.Any(candle =>
                candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromMinutes((int)candle.Interval)))
            return null;

        var entry = signalCandles[^1].Close;
        var previous = signalCandles.SkipLast(1).TakeLast(shortChannel).ToArray();
        if (entry <= previous.Max(candle => candle.High))
            return null;
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(signalCandles).Value;
        if (atr is not > 0m)
            return null;

        decimal stop;
        decimal target;
        decimal risk;
        try
        {
            stop = previous.Min(candle => candle.Low) - atr.Value * stopBufferAtr;
            risk = entry - stop;
            target = entry + risk * targetRiskMultiple;
            if (stop <= 0m || risk <= 0m || target <= entry)
                return null;
        }
        catch (OverflowException)
        {
            return null;
        }

        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, entry, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(signalCandles), _profile.Identity),
            signalCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Frozen prior {shortChannel}-candle low, ATR {atrPeriod} buffer {stopBufferAtr}; estimated target {target} at {targetRiskMultiple:F1} gross risk multiples. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateBollingerStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        int bandPeriod,
        decimal bandDeviation,
        int atrPeriod,
        decimal stopBufferAtr,
        decimal minimumRewardRisk)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || bandPeriod is < 5 or > 100 || bandDeviation is < .5m or > 4m
            || atrPeriod is < 5 or > 50 || stopBufferAtr is < .1m or > 2m
            || minimumRewardRisk is < .5m or > 2m
            || signalCandles.Count != Math.Max(bandPeriod, atrPeriod) + 1
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || signalCandles.Zip(signalCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || signalCandles.Any(candle =>
                candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromMinutes((int)candle.Interval)
                || candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close)))
            return null;

        var previous = signalCandles.SkipLast(1).ToArray();
        var current = signalCandles[^1];
        var prior = previous[^1];
        var priorBands = new BollingerBandsCalculator(bandPeriod, bandDeviation).Calculate(previous).Value;
        var currentBands = new BollingerBandsCalculator(bandPeriod, bandDeviation).Calculate(signalCandles).Value;
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(signalCandles).Value;
        if (priorBands is not { } previousBand || currentBands is not { } signalBand
            || atr is not > 0m
            || prior.Close >= previousBand.Lower
            || current.Close <= signalBand.Lower || current.Close >= signalBand.Middle)
            return null;

        decimal stop;
        decimal rewardRisk;
        try
        {
            stop = Math.Min(prior.Low, current.Low) - atr.Value * stopBufferAtr;
            if (stop <= 0m || stop >= current.Close || signalBand.Middle <= current.Close)
                return null;
            rewardRisk = (signalBand.Middle - current.Close) / (current.Close - stop);
            if (rewardRisk < minimumRewardRisk)
                return null;
        }
        catch (OverflowException)
        {
            return null;
        }

        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, current.Close, stop, signalBand.Middle,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(signalCandles), _profile.Identity),
            signalCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Frozen excursion/re-entry low {Math.Min(prior.Low, current.Low)}, ATR {atrPeriod} buffer {stopBufferAtr}; estimated signal middle-band target {signalBand.Middle}, gross reward/risk {rewardRisk:F2}. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateRsiPullbackStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        int stopSwingLookback,
        int rsiPeriod,
        decimal pullbackRsiMinimum,
        decimal pullbackRsiMaximum,
        int signalEmaPeriod,
        int volumePeriod,
        int atrPeriod,
        decimal stopBufferAtr,
        decimal targetRiskMultiple)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || stopSwingLookback is < 3 or > 20 || rsiPeriod is < 5 or > 50
            || pullbackRsiMinimum is < 10m or > 50m
            || pullbackRsiMaximum is < 20m or > 60m
            || pullbackRsiMinimum >= pullbackRsiMaximum
            || signalEmaPeriod is < 5 or > 100 || volumePeriod is < 5 or > 100
            || atrPeriod is < 5 or > 50 || stopBufferAtr is < .1m or > 2m
            || targetRiskMultiple is < 1m or > 4m
            || signalCandles.Count != 320
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || signalCandles.Zip(signalCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || signalCandles.Any(candle =>
                candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromMinutes((int)candle.Interval)
                || candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close)))
            return null;

        var previous = signalCandles.SkipLast(1).ToArray();
        var current = signalCandles[^1];
        var prior = previous[^1];
        var priorRsi = new RelativeStrengthIndexCalculator(rsiPeriod).Calculate(previous).Value;
        var currentRsi = new RelativeStrengthIndexCalculator(rsiPeriod).Calculate(signalCandles).Value;
        var signalEma = new ExponentialMovingAverageCalculator(signalEmaPeriod).Calculate(signalCandles).Value;
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(signalCandles).Value;
        if (priorRsi is null || currentRsi is null || signalEma is null || atr is not > 0m
            || priorRsi < pullbackRsiMinimum || priorRsi > pullbackRsiMaximum
            || currentRsi <= priorRsi
            || !(current.Close > signalEma || current.Close > prior.High)
            || current.Volume < previous.TakeLast(volumePeriod).Average(candle => candle.Volume))
            return null;

        decimal stop;
        decimal target;
        var swingLow = signalCandles.TakeLast(stopSwingLookback).Min(candle => candle.Low);
        try
        {
            stop = swingLow - atr.Value * stopBufferAtr;
            if (stop <= 0m || stop >= current.Close)
                return null;
            target = current.Close + (current.Close - stop) * targetRiskMultiple;
        }
        catch (OverflowException)
        {
            return null;
        }

        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, current.Close, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(signalCandles), _profile.Identity),
            signalCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Frozen {stopSwingLookback}-candle pullback low {swingLow}, ATR {atrPeriod} buffer {stopBufferAtr}; estimated target {target} at {targetRiskMultiple:F1} gross signal risk multiples. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateMacdCrossStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        int stopSwingLookback,
        int macdFast,
        int macdSlow,
        int macdSignal,
        int volumePeriod,
        decimal volumeMultiplier,
        int atrPeriod,
        decimal stopBufferAtr,
        decimal targetRiskMultiple)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || stopSwingLookback is < 3 or > 20 || macdFast is < 5 or > 30
            || macdSlow is < 15 or > 60 || macdFast >= macdSlow
            || macdSignal is < 3 or > 20 || volumePeriod is < 5 or > 100
            || volumeMultiplier is < .5m or > 3m
            || atrPeriod is < 5 or > 50 || stopBufferAtr is < .1m or > 2m
            || targetRiskMultiple is < 1m or > 4m
            || signalCandles.Count != 320
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || signalCandles.Zip(signalCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || signalCandles.Any(candle =>
                candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromMinutes((int)candle.Interval)
                || candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close)))
            return null;

        var prior = signalCandles.SkipLast(1).ToArray();
        var current = signalCandles[^1];
        var calculator = new MovingAverageConvergenceDivergenceCalculator(
            macdFast, macdSlow, macdSignal);
        var priorMacd = calculator.Calculate(prior).Value;
        var macd = calculator.Calculate(signalCandles).Value;
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(signalCandles).Value;
        if (priorMacd is not { } previous || macd is not { } crossing
            || atr is not > 0m
            || previous.Line > previous.Signal || crossing.Line <= crossing.Signal
            || current.Volume < prior.TakeLast(volumePeriod).Average(candle => candle.Volume)
                * volumeMultiplier)
            return null;

        decimal stop;
        decimal target;
        var swingLow = signalCandles.TakeLast(stopSwingLookback).Min(candle => candle.Low);
        try
        {
            stop = swingLow - atr.Value * stopBufferAtr;
            if (stop <= 0m || stop >= current.Close)
                return null;
            target = current.Close + (current.Close - stop) * targetRiskMultiple;
        }
        catch (OverflowException)
        {
            return null;
        }

        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, current.Close, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(signalCandles), _profile.Identity),
            signalCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Frozen {stopSwingLookback}-candle crossing swing low {swingLow}, ATR {atrPeriod} buffer {stopBufferAtr}; estimated target {target} at {targetRiskMultiple:F1} gross signal risk multiples. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateEmaPullbackStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        int stopSwingLookback,
        int signalPullbackEma,
        int signalInvalidationEma,
        int volumePeriod,
        int atrPeriod,
        decimal stopBufferAtr,
        decimal targetRiskMultiple)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || stopSwingLookback is < 3 or > 20 || signalPullbackEma is < 5 or > 100
            || signalInvalidationEma is < 10 or > 150 || volumePeriod is < 5 or > 100
            || atrPeriod is < 5 or > 50 || stopBufferAtr is < .1m or > 2m
            || targetRiskMultiple is < 1m or > 4m
            || signalCandles.Count != 320
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || signalCandles.Zip(signalCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || signalCandles.Any(candle =>
                candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromMinutes((int)candle.Interval)
                || candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close)))
            return null;

        var prior = signalCandles.SkipLast(1).ToArray();
        var pullback = prior[^1];
        var current = signalCandles[^1];
        var priorFast = new ExponentialMovingAverageCalculator(signalPullbackEma).Calculate(prior).Value;
        var currentFast = new ExponentialMovingAverageCalculator(signalPullbackEma).Calculate(signalCandles).Value;
        var currentMedium = new ExponentialMovingAverageCalculator(signalInvalidationEma).Calculate(signalCandles).Value;
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(signalCandles).Value;
        if (priorFast is null || currentFast is null || currentMedium is null || atr is not > 0m
            || pullback.Low > priorFast
            || pullback.Close < currentMedium
            || current.Close <= currentFast
            || current.Volume < prior.TakeLast(volumePeriod).Average(candle => candle.Volume))
            return null;

        decimal stop;
        decimal target;
        var swingLow = signalCandles.TakeLast(stopSwingLookback).Min(candle => candle.Low);
        try
        {
            stop = swingLow - atr.Value * stopBufferAtr;
            if (stop <= 0m || stop >= current.Close)
                return null;
            target = current.Close + (current.Close - stop) * targetRiskMultiple;
        }
        catch (OverflowException)
        {
            return null;
        }

        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, current.Close, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(signalCandles), _profile.Identity),
            signalCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Frozen {stopSwingLookback}-candle EMA pullback low {swingLow}, ATR {atrPeriod} buffer {stopBufferAtr}; estimated target {target} at {targetRiskMultiple:F1} gross signal risk multiples. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateCompressionRangeStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        int channelPeriod,
        int volumePeriod,
        decimal volumeMultiplier,
        int atrPeriod,
        decimal maximumExtensionAtr,
        decimal stopBufferAtr,
        decimal targetRiskMultiple)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || channelPeriod is < 5 or > 100 || volumePeriod is < 5 or > 100
            || volumeMultiplier is < 1m or > 3m || atrPeriod is < 5 or > 50
            || maximumExtensionAtr is < .1m or > 5m || stopBufferAtr is < .1m or > 2m
            || targetRiskMultiple is < 1m or > 4m
            || signalCandles.Count != 320
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || signalCandles.Zip(signalCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || signalCandles.Any(candle =>
                candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromMinutes((int)candle.Interval)
                || candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close)))
            return null;

        var previous = signalCandles.SkipLast(1).ToArray();
        var current = signalCandles[^1];
        var priorChannel = previous.TakeLast(channelPeriod).ToArray();
        var upper = priorChannel.Max(candle => candle.High);
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(signalCandles).Value;
        if (atr is not > 0m || current.Close <= upper
            || current.Close - upper > atr.Value * maximumExtensionAtr
            || current.Volume < previous.TakeLast(volumePeriod).Average(candle => candle.Volume)
                * volumeMultiplier)
            return null;

        decimal stop;
        decimal target;
        var lower = priorChannel.Min(candle => candle.Low);
        try
        {
            stop = lower - atr.Value * stopBufferAtr;
            if (stop <= 0m || stop >= current.Close)
                return null;
            target = current.Close + (current.Close - stop) * targetRiskMultiple;
        }
        catch (OverflowException)
        {
            return null;
        }

        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, current.Close, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(signalCandles), _profile.Identity),
            signalCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Frozen prior {channelPeriod}-candle compressed range {lower}-{upper}, ATR {atrPeriod} buffer {stopBufferAtr}; estimated target {target} at {targetRiskMultiple:F1} gross signal risk multiples. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateMomentumDailyStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> dailyCandles,
        int stopSwingLookback,
        int trendEmaPeriod,
        int trendLookback,
        int atrPeriod,
        decimal stopBufferAtr,
        decimal targetRiskMultiple)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(dailyCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || stopSwingLookback is < 3 or > 30 || trendEmaPeriod is < 50 or > 300
            || trendLookback is < 30 or > 180 || atrPeriod is < 5 or > 50
            || stopBufferAtr is < .1m or > 2m || targetRiskMultiple is < 1m or > 4m
            || dailyCandles.Count != 320
            || !HasSafeClosedEvidence(dailyCandles, observation.ObservedAtUtc)
            || dailyCandles.Zip(dailyCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || dailyCandles.Any(candle =>
                candle.Interval != CandleInterval.OneDay
                || candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromDays(1)
                || candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close) || candle.Volume <= 0m))
            return null;

        var current = dailyCandles[^1];
        var trend = new ExponentialMovingAverageCalculator(trendEmaPeriod).Calculate(dailyCandles).Value;
        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(dailyCandles).Value;
        if (trend is null || atr is not > 0m || current.Close <= trend
            || current.Close <= dailyCandles[^(trendLookback + 1)].Close)
            return null;

        decimal stop;
        decimal target;
        var swingLow = dailyCandles.SkipLast(1).TakeLast(stopSwingLookback).Min(candle => candle.Low);
        try
        {
            stop = swingLow - atr.Value * stopBufferAtr;
            if (stop <= 0m || stop >= current.Close)
                return null;
            target = current.Close + (current.Close - stop) * targetRiskMultiple;
        }
        catch (OverflowException)
        {
            return null;
        }

        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, current.Close, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(dailyCandles), _profile.Identity),
            dailyCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Frozen prior {stopSwingLookback}-day swing low {swingLow}, ATR {atrPeriod} buffer {stopBufferAtr}; estimated target {target} at {targetRiskMultiple:F1} gross signal risk multiples. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateThreeSwingPivotStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        IReadOnlyList<Candle> hourlyCandles,
        IReadOnlyList<Candle> fourHourlyCandles,
        ThreeSwingPaperPlanRules rules)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        ArgumentNullException.ThrowIfNull(hourlyCandles);
        ArgumentNullException.ThrowIfNull(fourHourlyCandles);
        ArgumentNullException.ThrowIfNull(rules);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || signalCandles.Count != 320 || hourlyCandles.Count != 320 || fourHourlyCandles.Count != 320
            || rules.MinimumAlignedContextTimeframes is < 1 or > 2
            || rules.AtrPeriod is < 5 or > 50
            || rules.StopBufferAtr is < .1m or > 2m || rules.TargetRiskMultiple is < 1m or > 4m
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || !HasSafeClosedEvidence(hourlyCandles, hourlyCandles[^1].CloseTimeUtc)
            || !HasSafeClosedEvidence(fourHourlyCandles, fourHourlyCandles[^1].CloseTimeUtc)
            || hourlyCandles[^1].CloseTimeUtc != AlignDown(observation.ObservedAtUtc, TimeSpan.FromHours(1))
            || fourHourlyCandles[^1].CloseTimeUtc != AlignDown(observation.ObservedAtUtc, TimeSpan.FromHours(4))
            || signalCandles.Concat(hourlyCandles).Concat(fourHourlyCandles)
                .Any(candle => candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                    || candle.High < Math.Max(candle.Open, candle.Close) || candle.Volume <= 0m))
            return null;

        var evidence = ThreeSwingChannelDivergenceModel.Evaluate(
            signalCandles, hourlyCandles, fourHourlyCandles,
            rules.RsiPeriod, rules.MacdFast, rules.MacdSlow, rules.MacdSignal,
            rules.ChannelPeriod, rules.PivotSideBars, rules.MaximumPivotLookback,
            rules.ChannelProximityPercent, rules.ContextBearishMinimumPercent,
            rules.ContextBullishMaximumPercent, rules.MinimumRsiDivergencePoints,
            rules.MinimumPriceProgressPercent);
        if (!evidence.IsAvailable || evidence.Direction != ThreeSwingDivergenceDirection.Bullish
            || evidence.Pivots.Count != 3 || !evidence.IsNearFiveMinuteChannel
            || (evidence.IsOneHourContextAligned ? 1 : 0)
                + (evidence.IsFourHourContextAligned ? 1 : 0) < rules.MinimumAlignedContextTimeframes
            || rules.RequireClosedReversal && !evidence.HasReversalConfirmation
            || rules.RequireMacdConfirmation && !evidence.HasMacdConfirmation)
            return null;

        var atr = new AverageTrueRangeCalculator(rules.AtrPeriod).Calculate(signalCandles).Value;
        if (atr is not > 0m)
            return null;
        var thirdPivot = evidence.Pivots[^1];
        var invalidationLow = signalCandles.Skip(thirdPivot.CandleIndex)
            .Min(candle => candle.Low);
        decimal stop;
        decimal target;
        try
        {
            stop = invalidationLow - atr.Value * rules.StopBufferAtr;
            if (stop <= 0m || stop >= signalCandles[^1].Close)
                return null;
            target = signalCandles[^1].Close
                + (signalCandles[^1].Close - stop) * rules.TargetRiskMultiple;
        }
        catch (OverflowException)
        {
            return null;
        }
        var sources = signalCandles.Concat(hourlyCandles).Concat(fourHourlyCandles).ToArray();
        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, signalCandles[^1].Close, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                EvidenceIdentity(sources), _profile.Identity),
            sources.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Confirmed bullish pivots {string.Join(", ", evidence.Pivots.Select(pivot => FormattableString.Invariant($"{pivot.CloseTimeUtc:O} {pivot.Price} / RSI {pivot.Rsi:F2}")))}; frozen third-swing/reversal low {invalidationLow} with ATR {rules.AtrPeriod} buffer {rules.StopBufferAtr}; estimated target {target} at {rules.TargetRiskMultiple:F1} gross signal risk multiples. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    public PaperExecutionPlan? TryCreateRegimeSelectedSwingStructure(
        StrategyAnalysisProposal observation,
        IReadOnlyList<Candle> signalCandles,
        string selectedFamilyId,
        int selectedVersion,
        string componentFingerprint,
        int stopSwingLookback,
        int atrPeriod,
        decimal stopBufferAtr,
        decimal targetRiskMultiple)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(signalCandles);
        if (_profile.Version != 2 || !observation.TemplateId.Equals(_profile.TemplateId)
            || observation.Direction != StrategyAnalysisDirection.Bullish || observation.Confidence <= 0m
            || selectedFamilyId is not ("platform.ema-trend-continuation"
                or "platform.donchian-breakout-ensemble" or "platform.rsi-pullback"
                or "platform.bollinger-mean-reversion" or "platform.volatility-compression-breakout")
            || selectedVersion != 4
            || componentFingerprint?.Length != 64
            || !componentFingerprint.All(Uri.IsHexDigit)
            || stopSwingLookback is < 3 or > 20 || atrPeriod is < 5 or > 50
            || stopBufferAtr is < .1m or > 2m || targetRiskMultiple is < 1m or > 4m
            || signalCandles.Count != 320
            || !HasSafeClosedEvidence(signalCandles, observation.ObservedAtUtc)
            || signalCandles.Zip(signalCandles.Skip(1),
                (earlier, later) => earlier.CloseTimeUtc != later.OpenTimeUtc).Any(gap => gap)
            || signalCandles.Any(candle => candle.Interval != CandleInterval.FourHours
                || candle.CloseTimeUtc - candle.OpenTimeUtc != TimeSpan.FromHours(4)
                || candle.Low <= 0m || candle.Low > Math.Min(candle.Open, candle.Close)
                || candle.High < Math.Max(candle.Open, candle.Close) || candle.Volume <= 0m))
            return null;

        var atr = new AverageTrueRangeCalculator(atrPeriod).Calculate(signalCandles).Value;
        if (atr is not > 0m)
            return null;
        var priorLow = signalCandles.SkipLast(1).TakeLast(stopSwingLookback)
            .Min(candle => candle.Low);
        decimal stop;
        decimal target;
        try
        {
            stop = priorLow - atr.Value * stopBufferAtr;
            if (stop <= 0m || stop >= signalCandles[^1].Close)
                return null;
            target = signalCandles[^1].Close
                + (signalCandles[^1].Close - stop) * targetRiskMultiple;
        }
        catch (OverflowException)
        {
            return null;
        }
        var selectionEvidence = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{EvidenceIdentity(signalCandles)}|{selectedFamilyId}|{selectedVersion}|{componentFingerprint}")));
        return new PaperExecutionPlan(
            _profile.TemplateId, observation.Direction, signalCandles[^1].Close, stop, target,
            observation.ObservedAtUtc,
            new PaperExecutionPlanProvenance(_profile.Identity, _profile.Version,
                selectionEvidence, _profile.Identity),
            signalCandles.Select(candle => new PaperExecutionPlanCandleIdentity(
                candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc)),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{observation.Rationale} Selected {selectedFamilyId} v{selectedVersion} decision {componentFingerprint}; independent ensemble stop at prior {stopSwingLookback}-candle four-hour low {priorLow} minus ATR {atrPeriod} buffer {stopBufferAtr}; estimated target {target} at {targetRiskMultiple:F1} gross signal risk multiples. Not the selected component's own stop. Paper plan only; no sizing, order, intent, or execution is created."));
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - value.UtcTicks % interval.Ticks, TimeSpan.Zero);

    private static string EvidenceIdentity(IEnumerable<Candle> candles) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "|",
            candles.Select(candle => FormattableString.Invariant(
                $"{candle.Symbol}:{(int)candle.Interval}:{candle.OpenTimeUtc:O}:{candle.CloseTimeUtc:O}:{candle.Open}:{candle.High}:{candle.Low}:{candle.Close}:{candle.Volume}"))))));

    private static bool HasSafeClosedEvidence(IReadOnlyList<Candle> candles, DateTimeOffset asOfUtc)
    {
        if (candles.Count == 0 || asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero)
            return false;

        var first = candles[0];
        if (first is null)
            return false;

        Candle? previous = null;
        foreach (var candle in candles)
        {
            if (candle is null || !candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal
                || candle.CloseTimeUtc > asOfUtc || candle.Interval != first.Interval
                || !string.Equals(candle.Symbol, first.Symbol, StringComparison.OrdinalIgnoreCase)
                || (previous is not null && (candle.OpenTimeUtc <= previous.OpenTimeUtc || candle.CloseTimeUtc <= previous.CloseTimeUtc)))
                return false;
            previous = candle;
        }

        return candles[^1].CloseTimeUtc == asOfUtc;
    }
}
