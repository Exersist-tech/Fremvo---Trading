using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Sessions;
using Trading.Domain.Strategies;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// Platform-authored, versioned breakout research that reports a session-conditioned observation
/// beside an explicit, otherwise identical no-session baseline. It is analysis only.
/// </summary>
public sealed class SessionConditionedBreakoutResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string ChannelPeriodParameter = "channelPeriod";
    private const string BreakoutBufferPercentParameter = "breakoutBufferPercent";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(ChannelPeriodParameter, 2m, 80m, 20m, "Completed preceding signal candles in the decimal high-low channel.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(BreakoutBufferPercentParameter, 0m, 5m, 0m, "Inclusive decimal percentage buffer beyond the preceding channel boundary.", valueType: StrategyParameterValueType.Numeric)
        ]);

    public SessionConditionedBreakoutResearchModel()
        : base(
            "session-conditioned-breakout-v1",
            "Session-Conditioned Breakout Research",
            TradingProductType.Spot,
            "Closed-candle decimal channel breakout research paired with an explicit no-session baseline.",
            "channel: 2-80 completed candles; breakout buffer: 0-5%",
            "Both outputs use the same safe, closed, chronological as-of-aligned evidence. The configured output additionally requires UTC membership in the supplied versioned session profile at the evaluated candidate.",
            "Requires accepted rejection gates, configured Regime/Signal/Execution roles, sufficient completed warmup, safe as-of-aligned candles, and a platform-owned session profile.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("session-conditioned-breakout-v1");
    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        return Neutral(input, "Unavailable: a session profile and accepted rejection-gate evidence are required before session-conditioned breakout research evaluation.");
    }

    public SessionConditionedBreakoutResearchComparison Evaluate(SessionConditionedBreakoutEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var input = evaluation.EvaluationInput;
        ValidateInput(input);

        // The candidate is the newest safe closed execution instant, never a rewritten subset of history.
        var membership = SessionMembershipEngine.Evaluate(evaluation.SessionProfile, input.AsOfUtc);
        var metadata = new SessionBreakoutResearchMetadata(
            evaluation.SessionProfile.Identity,
            membership.Evidence,
            input.AsOfUtc,
            input.Signal.ClosedCandles.Count);

        if (!evaluation.HasAcceptedGatesAt(input.AsOfUtc))
        {
            return Pair(
                Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle."),
                Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle."),
                metadata);
        }

        var period = Period(input, ChannelPeriodParameter);
        if (input.Signal.ClosedCandles.Count < period + 1 || input.Execution.ClosedCandles.Count < 1)
        {
            var unavailable = Neutral(input, string.Create(CultureInfo.InvariantCulture, $"Unavailable: requires {period + 1} signal and 1 execution completed candle."));
            return Pair(unavailable, unavailable, metadata);
        }

        var baseline = EvaluateChannel(input, period);
        var conditioned = membership.IsMember
            ? baseline
            : Neutral(input, "No condition: the evaluated UTC candidate is outside the configured session profile; the no-session baseline remains independently evaluated.");
        return Pair(conditioned, baseline, metadata);
    }

    private static SessionConditionedBreakoutResearchComparison Pair(
        StrategyAnalysisProposal sessionConditioned,
        StrategyAnalysisProposal noSessionBaseline,
        SessionBreakoutResearchMetadata metadata) =>
        new(
            new SessionBreakoutResearchOutput(sessionConditioned, true, metadata),
            new SessionBreakoutResearchOutput(noSessionBaseline, false, metadata));

    private StrategyAnalysisProposal EvaluateChannel(StrategyEvaluationInput input, int period)
    {
        var candles = input.Signal.ClosedCandles;
        var candidate = candles[^1];
        var prior = candles.Skip(candles.Count - period - 1).Take(period).ToArray();
        var upper = prior.Max(candle => candle.High);
        var lower = prior.Min(candle => candle.Low);
        var buffer = input.Parameters.GetDecimal(BreakoutBufferPercentParameter) / 100m;
        var upperBoundary = upper * (1m + buffer);
        var lowerBoundary = lower * (1m - buffer);
        var direction = candidate.Close > upperBoundary
            ? StrategyAnalysisDirection.Bullish
            : candidate.Close < lowerBoundary
                ? StrategyAnalysisDirection.Bearish
                : StrategyAnalysisDirection.Neutral;

        if (direction == StrategyAnalysisDirection.Neutral)
        {
            return Neutral(input, string.Create(CultureInfo.InvariantCulture, $"No condition: completed candidate close {candidate.Close:F4} did not strictly cross decimal channel boundaries {lowerBoundary:F4}-{upperBoundary:F4}."));
        }

        var boundary = direction == StrategyAnalysisDirection.Bullish ? upperBoundary : lowerBoundary;
        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            direction,
            0.50m,
            string.Create(CultureInfo.InvariantCulture, $"Completed decimal channel breakout observation: close {candidate.Close:F4} strictly crossed {boundary:F4} from a {period}-candle preceding channel; analysis only, not a trade instruction."));
    }

    private static StrategyAnalysisProposal Neutral(StrategyEvaluationInput input, string rationale) =>
        new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Neutral, 0m, rationale);

    private static int Period(StrategyEvaluationInput input, string name) =>
        decimal.ToInt32(input.Parameters.GetDecimal(name));

    private void ValidateInput(StrategyEvaluationInput input)
    {
        if (!TemplateId.Equals(input.TemplateId))
        {
            throw new ArgumentException("Evaluation input belongs to a different template.", nameof(input));
        }

        if (input.Parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("Session-conditioned breakout accepts only its platform-defined parameters.", nameof(input));
        }

        foreach (var expected in s_parameterDefinitions)
        {
            StrategyParameterDefinition actual;
            try { actual = input.Parameters.GetDefinition(expected.Name); }
            catch (KeyNotFoundException exception) { throw new ArgumentException($"Session-conditioned breakout requires parameter '{expected.Name}'.", nameof(input), exception); }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(input));
            }
        }
    }
}

/// <summary>Immutable provenance shared by both comparable observations.</summary>
public sealed record SessionBreakoutResearchMetadata(
    SessionProfileVersionIdentity ProfileIdentity,
    SessionMembershipEvidence SessionMembership,
    DateTimeOffset EvaluatedCandidateUtc,
    int SignalEvidenceCandleCount);

/// <summary>One explicit member of the paired research comparison.</summary>
public sealed record SessionBreakoutResearchOutput(
    StrategyAnalysisProposal Proposal,
    bool AppliesSessionCondition,
    SessionBreakoutResearchMetadata Metadata);

/// <summary>Contains both the configured profile result and an unconditioned baseline.</summary>
public sealed record SessionConditionedBreakoutResearchComparison(
    SessionBreakoutResearchOutput SessionConditioned,
    SessionBreakoutResearchOutput NoSessionBaseline);

/// <summary>Couples immutable evidence, recorded gates, and a platform-owned session profile.</summary>
public sealed class SessionConditionedBreakoutEvaluationInput
{
    public SessionConditionedBreakoutEvaluationInput(
        StrategyEvaluationInput evaluationInput,
        RejectionGateEvaluation? rejectionGates,
        SessionProfile sessionProfile)
    {
        EvaluationInput = evaluationInput ?? throw new ArgumentNullException(nameof(evaluationInput));
        RejectionGates = rejectionGates;
        SessionProfile = sessionProfile ?? throw new ArgumentNullException(nameof(sessionProfile));
    }

    public StrategyEvaluationInput EvaluationInput { get; }
    public RejectionGateEvaluation? RejectionGates { get; }
    public SessionProfile SessionProfile { get; }

    internal bool HasAcceptedGatesAt(DateTimeOffset asOfUtc) =>
        RejectionGates is { Accepted: true, Results.Count: > 0 }
        && RejectionGates.Results.All(result => result.Status == RejectionGateStatus.Passed && result.EvaluatedAtUtc == asOfUtc);
}
