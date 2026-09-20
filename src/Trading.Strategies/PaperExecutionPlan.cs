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
    internal ApprovedPaperExecutionPlanProfile(StrategyTemplateId templateId, string identity)
    {
        TemplateId = templateId;
        Identity = identity;
        Version = 1;
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
/// Registered, fixed adapters for the ten approved families. They only turn a
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
                "cross-sectional-momentum-rotation-v1", "relative-strength-pullback-rotation-v1",
                "session-conditioned-breakout-v1", "regime-switching-ensemble-v1"
            }.Select(id => new ApprovedPaperExecutionPlanProfile(new StrategyTemplateId(id), $"platform.paper-plan/{id}@1"))
             .ToDictionary(profile => profile.TemplateId));
    private readonly IReadOnlyDictionary<StrategyTemplateId, ApprovedPaperExecutionPlanProfile> _profiles = s_profiles;

    public IReadOnlyCollection<StrategyTemplateId> TemplateIds => _profiles.Keys.ToArray();

    public ApprovedPaperExecutionPlanAdapter GetRequired(StrategyTemplateId templateId) =>
        new(_profiles.TryGetValue(templateId ?? throw new ArgumentNullException(nameof(templateId)), out var profile)
            ? profile
            : throw new KeyNotFoundException($"No approved paper plan adapter is registered for '{templateId}'."));
}

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

    private static string EvidenceIdentity(IEnumerable<Candle> candles) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            "|",
            candles.Select(candle =>
                $"{candle.Symbol}:{(int)candle.Interval}:{candle.OpenTimeUtc:O}:{candle.CloseTimeUtc:O}:{candle.Open}:{candle.High}:{candle.Low}:{candle.Close}")))));

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
