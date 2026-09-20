using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored relative-strength pullback research model. It can only
/// report one explainable research candidate from a complete point-in-time universe.
/// </summary>
public sealed class RelativeStrengthPullbackRotationResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string MomentumLookbackPeriodsParameter = "momentumLookbackPeriods";
    private const string PullbackLookbackPeriodsParameter = "pullbackLookbackPeriods";
    private const string MinimumPullbackFractionParameter = "minimumPullbackFraction";
    private const string MaximumPullbackFractionParameter = "maximumPullbackFraction";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(MomentumLookbackPeriodsParameter, 2m, 365m, 90m, "Completed same-timeframe periods used for decimal relative-strength momentum.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(PullbackLookbackPeriodsParameter, 2m, 365m, 20m, "Completed same-timeframe periods used to find each member's own closing-price peak.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(MinimumPullbackFractionParameter, 0.001m, 0.50m, 0.03m, "Inclusive minimum decimal pullback from the member's own closing-price peak.", valueType: StrategyParameterValueType.Numeric),
            new StrategyParameterDefinition(MaximumPullbackFractionParameter, 0.001m, 0.50m, 0.10m, "Inclusive maximum decimal pullback from the member's own closing-price peak.", valueType: StrategyParameterValueType.Numeric)
        ]);

    public RelativeStrengthPullbackRotationResearchModel()
        : base(
            "relative-strength-pullback-rotation-v1",
            "Relative-Strength Pullback Rotation Research",
            TradingProductType.Spot,
            "Survivorship-aware decimal relative-strength ranking with a bounded pullback from each member's own closed-price history.",
            "momentum lookback: 2-365; pullback lookback: 2-365; pullback fraction: 0.001-0.50",
            "Every required universe member needs fresh membership/status evidence and safe, closed, same-timeframe history ending exactly at the UTC as-of time.",
            "Requires accepted rejection gates. Missing, stale, unavailable, delisted, unsafe, incomplete, incompatible, or future universe evidence blocks the entire ranking rather than excluding a member.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("relative-strength-pullback-rotation-v1");
    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TemplateId.Equals(input.TemplateId))
        {
            throw new ArgumentException("Evaluation input belongs to a different template.", nameof(input));
        }

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Neutral,
            0m,
            "Unavailable: relative-strength pullback rotation requires an immutable complete universe dataset and recorded rejection gates.");
    }

    public RelativeStrengthPullbackResearchResult Evaluate(RelativeStrengthPullbackRotationEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ValidateParameters(evaluation.Parameters);

        var dataset = evaluation.Dataset;
        if (!evaluation.HasAcceptedGatesAt(dataset.AsOfUtc))
        {
            return RelativeStrengthPullbackResearchResult.Blocked(
                dataset.AsOfUtc,
                "Blocked: rejection gates are missing, failed, or not aligned to the dataset UTC as-of time.");
        }

        var momentumLookback = decimal.ToInt32(evaluation.Parameters.GetDecimal(MomentumLookbackPeriodsParameter));
        var pullbackLookback = decimal.ToInt32(evaluation.Parameters.GetDecimal(PullbackLookbackPeriodsParameter));
        var minimumPullback = evaluation.Parameters.GetDecimal(MinimumPullbackFractionParameter);
        var maximumPullback = evaluation.Parameters.GetDecimal(MaximumPullbackFractionParameter);
        if (minimumPullback > maximumPullback)
        {
            return RelativeStrengthPullbackResearchResult.Blocked(
                dataset.AsOfUtc,
                "Blocked: minimum pullback fraction cannot exceed maximum pullback fraction.");
        }

        var requiredCandles = Math.Max(momentumLookback, pullbackLookback) + 1;
        var observations = new List<RelativeStrengthPullbackRankedInstrument>(dataset.UniverseMembers.Count);
        foreach (var member in dataset.UniverseMembers)
        {
            if (!member.IsMemberAtAsOf)
            {
                return Blocked(dataset, member.Symbol, "membership evidence does not confirm universe membership at as-of.");
            }

            if (member.Status != InstrumentTradingStatus.Trading)
            {
                return Blocked(dataset, member.Symbol, $"status '{member.Status}' is unavailable or not fully trading.");
            }

            if (member.EvidenceObservedAtUtc != dataset.AsOfUtc)
            {
                return Blocked(dataset, member.Symbol, "membership/status evidence is stale or not aligned to the dataset as-of.");
            }

            if (member.ClosedCandles is null || member.ClosedCandles.Count < requiredCandles)
            {
                return Blocked(dataset, member.Symbol, string.Create(CultureInfo.InvariantCulture, $"requires {requiredCandles} completed candles."));
            }

            if (!TryValidateHistory(member, dataset, out var reason))
            {
                return Blocked(dataset, member.Symbol, reason);
            }

            var history = member.ClosedCandles;
            var momentumStart = history[history.Count - 1 - momentumLookback].Close;
            var latestClose = history[^1].Close;
            var peak = history.Skip(history.Count - 1 - pullbackLookback).Max(candle => candle.Close);
            if (momentumStart <= 0m || latestClose <= 0m || peak <= 0m)
            {
                return Blocked(dataset, member.Symbol, "required closing prices must be positive.");
            }

            observations.Add(new RelativeStrengthPullbackRankedInstrument(
                member.InstrumentId,
                member.Symbol,
                (latestClose / momentumStart) - 1m,
                (peak - latestClose) / peak,
                0));
        }

        var ranked = observations
            .OrderByDescending(item => item.RelativeStrengthMomentum)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .Select((item, index) => item with { Rank = index + 1 })
            .ToArray();
        var candidate = ranked.FirstOrDefault(item =>
            item.PullbackFraction >= minimumPullback && item.PullbackFraction <= maximumPullback);
        if (candidate is null)
        {
            return RelativeStrengthPullbackResearchResult.Neutral(
                dataset.AsOfUtc,
                string.Create(CultureInfo.InvariantCulture, $"No candidate: none of {ranked.Length} complete universe members has a {minimumPullback:F3}-{maximumPullback:F3} bounded pullback from its own {pullbackLookback}-period closed-price history."));
        }

        return RelativeStrengthPullbackResearchResult.WithCandidate(
            dataset.AsOfUtc,
            candidate,
            string.Create(CultureInfo.InvariantCulture, $"Candidate: '{candidate.Symbol}' is relative-strength rank {candidate.Rank} by {momentumLookback}-period decimal momentum and has a {candidate.PullbackFraction:F6} bounded pullback from its own closed-price history; research only, not a trade instruction."));
    }

    private static RelativeStrengthPullbackResearchResult Blocked(
        CrossSectionalMomentumDataset dataset,
        string symbol,
        string detail) =>
        RelativeStrengthPullbackResearchResult.Blocked(
            dataset.AsOfUtc,
            string.Create(CultureInfo.InvariantCulture, $"Blocked: required universe member '{symbol}' cannot be excluded: {detail}"));

    private static bool TryValidateHistory(
        CrossSectionalMomentumUniverseMember member,
        CrossSectionalMomentumDataset dataset,
        out string reason)
    {
        var history = member.ClosedCandles!;
        if (history.Count > CrossSectionalMomentumDataset.MaximumCandlesPerMember)
        {
            reason = "history exceeds the immutable dataset bound.";
            return false;
        }

        Candle? previous = null;
        foreach (var candle in history)
        {
            if (candle is null
                || !candle.IsClosed
                || !candle.CanBeUsedForClosedCandleSignal
                || candle.Interval != dataset.Interval
                || !string.Equals(candle.Symbol, member.Symbol, StringComparison.OrdinalIgnoreCase)
                || candle.CloseTimeUtc > dataset.AsOfUtc)
            {
                reason = "history is unsafe, incompatible, or contains future data.";
                return false;
            }

            if (previous is not null
                && (candle.OpenTimeUtc <= previous.OpenTimeUtc || candle.CloseTimeUtc <= previous.CloseTimeUtc))
            {
                reason = "history is not strictly chronological.";
                return false;
            }

            previous = candle;
        }

        if (history[^1].CloseTimeUtc != dataset.AsOfUtc)
        {
            reason = "history is stale and does not end at the dataset as-of.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void ValidateParameters(StrategyParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("Relative-strength pullback rotation accepts only its platform-defined parameters.", nameof(parameters));
        }

        foreach (var expected in ParameterDefinitions)
        {
            StrategyParameterDefinition actual;
            try { actual = parameters.GetDefinition(expected.Name); }
            catch (KeyNotFoundException exception) { throw new ArgumentException($"Relative-strength pullback rotation requires parameter '{expected.Name}'.", nameof(parameters), exception); }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum
                || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required
                || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(parameters));
            }
        }
    }
}

public sealed class RelativeStrengthPullbackRotationEvaluationInput
{
    public RelativeStrengthPullbackRotationEvaluationInput(
        CrossSectionalMomentumDataset dataset,
        StrategyParameterSet parameters,
        RejectionGateEvaluation? rejectionGates)
    {
        Dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        RejectionGates = rejectionGates;
    }

    public CrossSectionalMomentumDataset Dataset { get; }
    public StrategyParameterSet Parameters { get; }
    public RejectionGateEvaluation? RejectionGates { get; }

    internal bool HasAcceptedGatesAt(DateTimeOffset asOfUtc) =>
        RejectionGates is { Accepted: true, Results.Count: > 0 }
        && RejectionGates.Results.All(result => result.Status == RejectionGateStatus.Passed && result.EvaluatedAtUtc == asOfUtc);
}

public enum RelativeStrengthPullbackResearchStatus { None = 0, Candidate = 1, Neutral = 2, Blocked = 3 }

public sealed record RelativeStrengthPullbackRankedInstrument(
    Guid InstrumentId,
    string Symbol,
    decimal RelativeStrengthMomentum,
    decimal PullbackFraction,
    int Rank);

public sealed class RelativeStrengthPullbackResearchResult
{
    private RelativeStrengthPullbackResearchResult(
        RelativeStrengthPullbackResearchStatus status,
        DateTimeOffset asOfUtc,
        RelativeStrengthPullbackRankedInstrument? candidate,
        string rationale)
    {
        Status = status;
        AsOfUtc = asOfUtc;
        Candidate = candidate;
        Rationale = rationale;
    }

    public RelativeStrengthPullbackResearchStatus Status { get; }
    public DateTimeOffset AsOfUtc { get; }
    public RelativeStrengthPullbackRankedInstrument? Candidate { get; }
    public string Rationale { get; }

    internal static RelativeStrengthPullbackResearchResult WithCandidate(
        DateTimeOffset asOfUtc,
        RelativeStrengthPullbackRankedInstrument candidate,
        string rationale) =>
        new(RelativeStrengthPullbackResearchStatus.Candidate, asOfUtc, candidate, rationale);

    internal static RelativeStrengthPullbackResearchResult Neutral(DateTimeOffset asOfUtc, string rationale) =>
        new(RelativeStrengthPullbackResearchStatus.Neutral, asOfUtc, null, rationale);

    internal static RelativeStrengthPullbackResearchResult Blocked(DateTimeOffset asOfUtc, string rationale) =>
        new(RelativeStrengthPullbackResearchStatus.Blocked, asOfUtc, null, rationale);
}
