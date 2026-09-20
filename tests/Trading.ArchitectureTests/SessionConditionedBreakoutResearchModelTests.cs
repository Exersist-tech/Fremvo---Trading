using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Sessions;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class SessionConditionedBreakoutResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 1, 5, 14, 30, 0, TimeSpan.Zero);
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ProducesComparableInSessionObservationAndExplicitBaseline()
    {
        var model = new SessionConditionedBreakoutResearchModel();
        var input = Input(model, [100m, 100m, 100m, 100m, 110m]);
        var result = model.Evaluate(new SessionConditionedBreakoutEvaluationInput(input, Gates(input), Profile(new TimeOnly(9, 0), new TimeOnly(10, 0))));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.SessionConditioned.Proposal.Direction);
        Assert.Equal(result.SessionConditioned.Proposal, result.NoSessionBaseline.Proposal);
        Assert.True(result.SessionConditioned.AppliesSessionCondition);
        Assert.False(result.NoSessionBaseline.AppliesSessionCondition);
        Assert.Equal(new SessionProfileVersionIdentity("platform.new-york", 1), result.NoSessionBaseline.Metadata.ProfileIdentity);
        Assert.True(result.SessionConditioned.Metadata.SessionMembership.SessionStartDate.HasValue);
        Assert.Equal(result.SessionConditioned.Metadata, result.NoSessionBaseline.Metadata);
    }

    [Fact]
    public void OutsideSessionBlocksOnlyConditionedOutputWithoutFilteringBaselineEvidence()
    {
        var model = new SessionConditionedBreakoutResearchModel();
        var input = Input(model, [100m, 100m, 100m, 100m, 110m], s_asOfUtc.AddHours(3));
        var result = model.Evaluate(new SessionConditionedBreakoutEvaluationInput(input, Gates(input), Profile(new TimeOnly(9, 0), new TimeOnly(10, 0))));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.SessionConditioned.Proposal.Direction);
        Assert.Contains("outside", result.SessionConditioned.Proposal.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StrategyAnalysisDirection.Bullish, result.NoSessionBaseline.Proposal.Direction);
        Assert.Equal(input.Signal.ClosedCandles.Count, result.NoSessionBaseline.Metadata.SignalEvidenceCandleCount);
        Assert.Equal(result.SessionConditioned.Metadata.EvaluatedCandidateUtc, result.NoSessionBaseline.Metadata.EvaluatedCandidateUtc);
    }

    [Fact]
    public void EnforcesStrictChannelBoundaryAndWarmupForBothOutputs()
    {
        var model = new SessionConditionedBreakoutResearchModel();
        var boundary = Input(model, [100m, 100m, 100m, 100m, 101m]);
        var warmup = Input(model, [100m, 100m, 100m, 100m]);

        var boundaryResult = model.Evaluate(new SessionConditionedBreakoutEvaluationInput(boundary, Gates(boundary), Profile(new TimeOnly(9, 0), new TimeOnly(10, 0))));
        var warmupResult = model.Evaluate(new SessionConditionedBreakoutEvaluationInput(warmup, Gates(warmup), Profile(new TimeOnly(9, 0), new TimeOnly(10, 0))));

        Assert.Equal(StrategyAnalysisDirection.Neutral, boundaryResult.NoSessionBaseline.Proposal.Direction);
        Assert.Contains("did not strictly cross", boundaryResult.NoSessionBaseline.Proposal.Rationale, StringComparison.Ordinal);
        Assert.All([warmupResult.SessionConditioned, warmupResult.NoSessionBaseline], output => Assert.Contains("requires 5", output.Proposal.Rationale, StringComparison.Ordinal));
    }

    [Fact]
    public void UsesDstResolvedCandidateMembershipAndBlocksBothOnGates()
    {
        var model = new SessionConditionedBreakoutResearchModel();
        var asOf = new DateTimeOffset(2026, 11, 1, 6, 45, 0, TimeSpan.Zero);
        var input = Input(model, [100m, 100m, 100m, 100m, 110m], asOf);
        var profile = new SessionProfile(new SessionProfileVersionIdentity("platform.dst", 2), "DST", "America/New_York", new TimeOnly(1, 30), new TimeOnly(2, 30), Enum.GetValues<DayOfWeek>(), SessionCrossMidnightBehavior.SameLocalDay, new SessionDstPolicy(SessionInvalidLocalTimePolicy.RejectOccurrence, SessionAmbiguousLocalTimePolicy.PreferLaterUtcInstant));

        var result = model.Evaluate(new SessionConditionedBreakoutEvaluationInput(input, Gates(input, false), profile));

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), result.SessionConditioned.Metadata.SessionMembership.StartBoundary!.UtcInstant);
        Assert.All([result.SessionConditioned, result.NoSessionBaseline], output => Assert.Contains("rejection gates", output.Proposal.Rationale, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsUnsafeFutureAndInvalidParametersAndHasNoExecutionSurface()
    {
        var model = new SessionConditionedBreakoutResearchModel();
        var configuration = new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var future = Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, [100m, 100m, 100m, 100m, 110m], s_asOfUtc.AddMinutes(5));
        var unsafeCandle = new Candle("BTCUSD", CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-5), s_asOfUtc, 99m, 101m, 99m, 100m, 1m, true, false, [DataQualityIssue.Stale]);
        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [Regime(), future, Execution(s_asOfUtc)]));
        Assert.Throws<ArgumentException>(() => new StrategyTimeframeSeries(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [unsafeCandle]));
        var values = Values(); values["breakoutBufferPercent"] = StrategyParameterValue.FromNumeric(5.01m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(model.ParameterDefinitions, values));
        Assert.DoesNotContain(typeof(SessionConditionedBreakoutResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance), member => member is MethodInfo method && (typeof(TradeIntent).IsAssignableFrom(method.ReturnType) || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType))));
    }

    [Fact]
    public void IsDeterministicForBothPairedOutputs()
    {
        var model = new SessionConditionedBreakoutResearchModel();
        var input = Input(model, [100m, 100m, 100m, 100m, 110m]);
        var evaluation = new SessionConditionedBreakoutEvaluationInput(input, Gates(input), Profile(new TimeOnly(9, 0), new TimeOnly(10, 0)));

        var first = model.Evaluate(evaluation);
        var second = model.Evaluate(evaluation);

        Assert.Equal(first.SessionConditioned.Proposal.Direction, second.SessionConditioned.Proposal.Direction);
        Assert.Equal(first.SessionConditioned.Proposal.Confidence, second.SessionConditioned.Proposal.Confidence);
        Assert.Equal(first.SessionConditioned.Proposal.Rationale, second.SessionConditioned.Proposal.Rationale);
        Assert.Equal(first.NoSessionBaseline.Proposal.Direction, second.NoSessionBaseline.Proposal.Direction);
        Assert.Equal(first.NoSessionBaseline.Proposal.Confidence, second.NoSessionBaseline.Proposal.Confidence);
        Assert.Equal(first.NoSessionBaseline.Proposal.Rationale, second.NoSessionBaseline.Proposal.Rationale);
        Assert.Equal(first.SessionConditioned.Metadata, first.NoSessionBaseline.Metadata);
    }

    private static StrategyEvaluationInput Input(SessionConditionedBreakoutResearchModel model, IEnumerable<decimal> closes, DateTimeOffset? asOf = null) =>
        new(model.TemplateId, Parameters(model), State(model), new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes), [Regime(), Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, closes, asOf ?? s_asOfUtc), Execution(asOf ?? s_asOfUtc)]);
    private static StrategyState State(SessionConditionedBreakoutResearchModel model) => new(model.TemplateId, 0, s_asOfUtc.AddHours(-1));
    private static StrategyParameterSet Parameters(SessionConditionedBreakoutResearchModel model) => new(model.ParameterDefinitions, Values());
    private static Dictionary<string, StrategyParameterValue> Values() => new() { ["channelPeriod"] = StrategyParameterValue.WholeNumber(4m), ["breakoutBufferPercent"] = StrategyParameterValue.FromNumeric(0m) };
    private static StrategyTimeframeSeries Regime() => Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, [100m], s_asOfUtc);
    private static StrategyTimeframeSeries Execution(DateTimeOffset asOf) => Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [100m], asOf);
    private static StrategyTimeframeSeries Series(StrategyTimeframeRole role, CandleInterval interval, IEnumerable<decimal> closes, DateTimeOffset asOf)
    {
        var values = closes.ToArray();
        var span = interval == CandleInterval.OneHour ? TimeSpan.FromHours(1) : interval == CandleInterval.FifteenMinutes ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(5);
        return new StrategyTimeframeSeries(role, interval, values.Select((value, index) => new Candle("BTCUSD", interval, asOf - span * (values.Length - index), asOf - span * (values.Length - index - 1), value - 1m, value + 1m, value - 1m, value, 1m, true, false, [])).ToArray());
    }
    private static SessionProfile Profile(TimeOnly start, TimeOnly end) => new(new SessionProfileVersionIdentity("platform.new-york", 1), "New York", "America/New_York", start, end, Enum.GetValues<DayOfWeek>(), SessionCrossMidnightBehavior.SameLocalDay, SessionDstPolicy.FailClosed);
    private static RejectionGateEvaluation Gates(StrategyEvaluationInput input, bool quality = true)
    {
        var instrument = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var approval = StrategyApproval.CreateDraft(Guid.NewGuid(), new StrategyVersion(new StrategyTemplateVersionIdentity("platform.session-conditioned-breakout", 1), new StrategyParameterSchemaReference("session-conditioned-breakout-parameters", 1, Fingerprint), Fingerprint, s_asOfUtc), StrategyApprovalActor.Human(Guid.NewGuid()), s_asOfUtc, new StrategyApprovalRequirements([new ApprovedInstrumentScope(AssetClass.Cryptocurrency, instrument)], 100, 100m, .10m, .01m, TimeSpan.FromMinutes(30), [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes], [TradingProductType.Spot], [StrategyApprovalMode.Backtest], input.Timeframes));
        var evidence = new StrategyResearchEvidence(new ResearchEvidenceProvenance("recorded-session-breakout-research", Fingerprint, input.AsOfUtc), new StrategyApprovalEvidence(instrument, AssetClass.Cryptocurrency, 100, 100m, .10m, .01m, input.AsOfUtc), quality, .03m, .02m, true, .01m, .90m, .90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(approval, input.Timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, input.AsOfUtc));
    }
}
