using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// Platform-authored, deterministic comparison of approved family observations.
/// It is research-only: it cannot size, allocate, increase exposure, or create
/// an intent or order.
/// </summary>
public sealed class RegimeSwitchingEnsembleResearchModel
{
    public const string EnsembleId = "platform.regime-switching-ensemble";
    public const int Version = 1;

    private static readonly ReadOnlyDictionary<MarketRegime, IReadOnlyList<string>> s_eligibleTemplateIds =
        new ReadOnlyDictionary<MarketRegime, IReadOnlyList<string>>(
            new Dictionary<MarketRegime, IReadOnlyList<string>>
            {
                [MarketRegime.TrendingUp] = Array.AsReadOnly(
                [
                    "ema-trend-continuation-v1",
                    "donchian-breakout-ensemble-v1",
                    "rsi-pullback-v1",
                    "macd-volume-trend-acceleration-v1",
                    "volatility-compression-breakout-v1"
                ]),
                [MarketRegime.TrendingDown] = Array.AsReadOnly(Array.Empty<string>()),
                [MarketRegime.Ranging] = Array.AsReadOnly(["bollinger-mean-reversion-v1"]),
                [MarketRegime.HighVolatility] = Array.AsReadOnly(Array.Empty<string>())
            });

    private readonly ReadOnlyDictionary<MarketRegime, IReadOnlyList<string>> _eligibleTemplateIds = s_eligibleTemplateIds;

    public static string MappingHash { get; } = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(
            "platform.regime-switching-ensemble@1|TrendingUp=ema-trend-continuation-v1,donchian-breakout-ensemble-v1,rsi-pullback-v1,macd-volume-trend-acceleration-v1,volatility-compression-breakout-v1|TrendingDown=|Ranging=bollinger-mean-reversion-v1|HighVolatility=")));

    public IReadOnlyDictionary<MarketRegime, IReadOnlyList<string>> EligibleTemplateIds => _eligibleTemplateIds;

    public RegimeSwitchingEnsembleResearchResult Evaluate(RegimeSwitchingEnsembleEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var classification = input.Classification;
        if (classification.Regime == MarketRegime.Unknown
            || !_eligibleTemplateIds.TryGetValue(classification.Regime, out var eligibleIds))
        {
            return RegimeSwitchingEnsembleResearchResult.Blocked(
                classification,
                "Blocked: the classifier regime is unknown or has no immutable platform mapping.");
        }

        if (!HasAcceptedGatesAt(input.RejectionGates, classification.AsOfUtc))
        {
            return RegimeSwitchingEnsembleResearchResult.Blocked(
                classification,
                "Blocked: rejection gates are missing, failed, or not aligned to the classifier as-of instant.");
        }

        if (eligibleIds.Count == 0)
        {
            return RegimeSwitchingEnsembleResearchResult.Neutral(
                classification,
                [],
                $"Neutral: regime '{classification.Regime}' has no approved Spot research family mapping; the ensemble cannot increase exposure.");
        }

        var selected = input.ComponentObservations
            .Where(component => eligibleIds.Contains(component.Proposal.TemplateId.Value, StringComparer.Ordinal))
            .OrderBy(component => component.Proposal.TemplateId.Value, StringComparer.Ordinal)
            .ToArray();

        if (selected.Length == 0)
        {
            return RegimeSwitchingEnsembleResearchResult.Neutral(
                classification,
                [],
                $"Neutral: no approved component observation was supplied for regime '{classification.Regime}'; the ensemble cannot increase exposure.");
        }

        if (!HasCompatibleSafeProvenance(classification.AsOfUtc, selected, out var reason))
        {
            return RegimeSwitchingEnsembleResearchResult.Blocked(classification, selected, reason);
        }

        var directional = selected
            .Where(component => component.Proposal.Direction != StrategyAnalysisDirection.Neutral)
            .ToArray();
        if (directional.Length == 0)
        {
            return RegimeSwitchingEnsembleResearchResult.Neutral(
                classification,
                selected,
                "Neutral: all selected approved component observations are neutral; their evidence is retained without selecting a default.");
        }

        var direction = directional[0].Proposal.Direction;
        if (directional.Any(component => component.Proposal.Direction != direction))
        {
            return RegimeSwitchingEnsembleResearchResult.Neutral(
                classification,
                selected,
                "Neutral: selected component observations disagree on direction; agreement affects research confidence only and cannot increase exposure.");
        }

        var averageConfidence = directional.Average(component => component.Proposal.Confidence);
        var agreementBonus = directional.Length > 1 ? 0.05m : 0m;
        var confidence = Math.Min(1m, averageConfidence + agreementBonus);
        return RegimeSwitchingEnsembleResearchResult.Observed(
            classification,
            selected,
            direction,
            confidence,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Research-only {direction} comparison: {directional.Length} selected approved component observation(s) agree; confidence {confidence:F4} reflects evidence agreement only and cannot increase exposure."));
    }

    private static bool HasAcceptedGatesAt(RejectionGateEvaluation? gates, DateTimeOffset asOfUtc) =>
        gates is { Accepted: true, Results.Count: > 0 }
        && gates.Results.All(result => result.Status == RejectionGateStatus.Passed && result.EvaluatedAtUtc == asOfUtc);

    private static bool HasCompatibleSafeProvenance(
        DateTimeOffset asOfUtc,
        RegimeSwitchingComponentObservation[] components,
        out string reason)
    {
        var first = components[0].Provenance;
        foreach (var component in components)
        {
            var proposal = component.Proposal;
            var provenance = component.Provenance;
            if (!provenance.IsSafe
                || proposal.ObservedAtUtc != asOfUtc
                || provenance.AsOfUtc != asOfUtc
                || provenance.AsOfUtc > asOfUtc)
            {
                reason = "Blocked: selected component provenance is unsafe, stale, or contains future evidence.";
                return false;
            }

            if (!string.Equals(provenance.DatasetId, first.DatasetId, StringComparison.Ordinal)
                || !string.Equals(provenance.Symbol, first.Symbol, StringComparison.OrdinalIgnoreCase)
                || !provenance.Timeframes.Equals(first.Timeframes))
            {
                reason = "Blocked: selected component observations do not share dataset, symbol, and timeframe provenance.";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }
}

/// <summary>Immutable source evidence retained beside a family observation.</summary>
public sealed class RegimeSwitchingObservationProvenance
{
    public RegimeSwitchingObservationProvenance(
        string datasetId,
        string symbol,
        StrategyTimeframeConfiguration timeframes,
        DateTimeOffset asOfUtc,
        bool isSafe)
    {
        if (string.IsNullOrWhiteSpace(datasetId) || string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Dataset and symbol provenance are required.");
        }

        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Provenance as-of must be explicit UTC.", nameof(asOfUtc));
        }

        DatasetId = datasetId.Trim();
        Symbol = symbol.Trim();
        Timeframes = timeframes ?? throw new ArgumentNullException(nameof(timeframes));
        AsOfUtc = asOfUtc;
        IsSafe = isSafe;
    }

    public string DatasetId { get; }
    public string Symbol { get; }
    public StrategyTimeframeConfiguration Timeframes { get; }
    public DateTimeOffset AsOfUtc { get; }
    public bool IsSafe { get; }
}

/// <summary>A preserved, approved-family observation with its point-in-time provenance.</summary>
public sealed class RegimeSwitchingComponentObservation
{
    public RegimeSwitchingComponentObservation(
        StrategyAnalysisProposal proposal,
        RegimeSwitchingObservationProvenance provenance)
    {
        Proposal = proposal ?? throw new ArgumentNullException(nameof(proposal));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public StrategyAnalysisProposal Proposal { get; }
    public RegimeSwitchingObservationProvenance Provenance { get; }
}

public sealed class RegimeSwitchingEnsembleEvaluationInput
{
    public RegimeSwitchingEnsembleEvaluationInput(
        RegimeClassification classification,
        IEnumerable<RegimeSwitchingComponentObservation> componentObservations,
        RejectionGateEvaluation? rejectionGates)
    {
        Classification = classification ?? throw new ArgumentNullException(nameof(classification));
        ArgumentNullException.ThrowIfNull(componentObservations);
        var copied = componentObservations.ToArray();
        if (copied.Length > 5 || copied.Any(component => component is null)
            || copied.Select(component => component.Proposal.TemplateId).Distinct().Count() != copied.Length)
        {
            throw new ArgumentException("At most five unique non-null component observations are permitted.", nameof(componentObservations));
        }

        ComponentObservations = new ReadOnlyCollection<RegimeSwitchingComponentObservation>(copied);
        RejectionGates = rejectionGates;
    }

    public RegimeClassification Classification { get; }
    public IReadOnlyList<RegimeSwitchingComponentObservation> ComponentObservations { get; }
    public RejectionGateEvaluation? RejectionGates { get; }
}

public enum RegimeSwitchingEnsembleStatus { None = 0, Observed = 1, Neutral = 2, Blocked = 3 }

/// <summary>Research evidence only. It contains no allocation, sizing, leverage, intent, or order surface.</summary>
public sealed class RegimeSwitchingEnsembleResearchResult
{
    private RegimeSwitchingEnsembleResearchResult(
        RegimeSwitchingEnsembleStatus status,
        RegimeClassification classification,
        IEnumerable<RegimeSwitchingComponentObservation> components,
        StrategyAnalysisDirection direction,
        decimal confidence,
        string rationale)
    {
        Status = status;
        EnsembleId = RegimeSwitchingEnsembleResearchModel.EnsembleId;
        EnsembleVersion = RegimeSwitchingEnsembleResearchModel.Version;
        MappingHash = RegimeSwitchingEnsembleResearchModel.MappingHash;
        Classification = classification;
        Components = new ReadOnlyCollection<RegimeSwitchingComponentObservation>(components.ToArray());
        Direction = direction;
        Confidence = confidence;
        Rationale = rationale;
    }

    public RegimeSwitchingEnsembleStatus Status { get; }
    public string EnsembleId { get; }
    public int EnsembleVersion { get; }
    public string MappingHash { get; }
    public RegimeClassification Classification { get; }
    public int ClassifierVersion => Classification.ClassifierVersion;
    public string ClassifierHash => Classification.ClassifierHash;
    public IReadOnlyList<RegimeSwitchingComponentObservation> Components { get; }
    public StrategyAnalysisDirection Direction { get; }
    public decimal Confidence { get; }
    public string Rationale { get; }

    internal static RegimeSwitchingEnsembleResearchResult Observed(
        RegimeClassification classification,
        IEnumerable<RegimeSwitchingComponentObservation> components,
        StrategyAnalysisDirection direction,
        decimal confidence,
        string rationale) =>
        new(RegimeSwitchingEnsembleStatus.Observed, classification, components, direction, confidence, rationale);

    internal static RegimeSwitchingEnsembleResearchResult Neutral(
        RegimeClassification classification,
        IEnumerable<RegimeSwitchingComponentObservation> components,
        string rationale) =>
        new(RegimeSwitchingEnsembleStatus.Neutral, classification, components, StrategyAnalysisDirection.Neutral, 0m, rationale);

    internal static RegimeSwitchingEnsembleResearchResult Blocked(
        RegimeClassification classification,
        string rationale) =>
        Blocked(classification, [], rationale);

    internal static RegimeSwitchingEnsembleResearchResult Blocked(
        RegimeClassification classification,
        IEnumerable<RegimeSwitchingComponentObservation> components,
        string rationale) =>
        new(RegimeSwitchingEnsembleStatus.Blocked, classification, components, StrategyAnalysisDirection.Neutral, 0m, rationale);
}
