using System.Reflection;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class RejectionGateTests
{
    private static readonly DateTimeOffset s_nowUtc =
        new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid s_instrumentId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Fingerprint =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public void PlatformGatesAcceptOnlyWhenEveryMandatoryGatePassesInStableOrder()
    {
        var result = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input());

        Assert.True(result.Accepted);
        Assert.Equal(
        [
            "approval-compliance",
            "required-requirements",
            "sufficient-history",
            "data-quality",
            "out-of-sample-holdout",
            "cost-liquidity-feasibility",
            "stable-reproducible"
        ],
        result.Results.Select(gate => gate.GateId));
        Assert.All(result.Results, gate => Assert.Equal(RejectionGateStatus.Passed, gate.Status));
    }

    [Fact]
    public void ASingleFailureOrUnknownEvidenceBlocksAggregateAcceptance()
    {
        var failed = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input(
            evidence: Evidence(outOfSampleNetReturn: -0.01m)));
        var unavailable = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input(
            evidence: Evidence(dataQualitySafe: null)));

        Assert.False(failed.Accepted);
        Assert.Equal(
            RejectionGateStatus.Failed,
            Assert.Single(failed.Results, result => result.GateId == "out-of-sample-holdout").Status);
        Assert.False(unavailable.Accepted);
        Assert.Equal(
            RejectionGateStatus.Unavailable,
            Assert.Single(unavailable.Results, result => result.GateId == "data-quality").Status);
    }

    [Fact]
    public void MissingOrStaleEvidenceFailsClosedAndRecordsSafeDetails()
    {
        var missing = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input(includeDefaultEvidence: false));
        var stale = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input(
            evidence: Evidence(asOfUtc: s_nowUtc.AddHours(-1))));

        Assert.False(missing.Accepted);
        Assert.Contains(missing.Results, result => result.Status == RejectionGateStatus.Unavailable);
        Assert.False(stale.Accepted);
        Assert.Contains(stale.Results, result => result.Status == RejectionGateStatus.Unavailable);
        Assert.All(missing.Results, result => Assert.False(string.IsNullOrWhiteSpace(result.Detail)));
    }

    [Fact]
    public void ResultsAreImmutableAndThresholdsHaveDistinctIdentities()
    {
        var result = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input());
        var properties = typeof(RejectionGateResult).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var lower = new RejectionGateDefinition(
            "stable-reproducible", 1, RejectionGateCategory.StableAndReproducible, 0.75m);
        var higher = new RejectionGateDefinition(
            "stable-reproducible", 1, RejectionGateCategory.StableAndReproducible, 0.80m);

        Assert.All(properties, property => Assert.Null(property.SetMethod));
        Assert.False(result.Results is List<RejectionGateResult>);
        Assert.NotEqual(lower.Identity, higher.Identity);
    }

    [Fact]
    public void GateEvaluationCannotAutomaticallyApproveOrExecuteAStrategy()
    {
        var human = StrategyApprovalActor.Human(Guid.NewGuid());
        var underReview = Approval(human).TransitionTo(
            StrategyApprovalState.UnderReview,
            human,
            s_nowUtc.AddMinutes(1));

        var result = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input(underReview));

        Assert.True(result.Accepted);
        Assert.Equal(StrategyApprovalState.UnderReview, underReview.State);
        Assert.DoesNotContain(
            typeof(StrategyRejectionGateEngine).Assembly.GetTypes(),
            type => type.Namespace == typeof(StrategyApproval).Namespace
                && type.Name.Contains("Execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GateResultsPreserveDecimalsAndUseUtcEvaluationTimestamps()
    {
        var result = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(Input(
            evidence: Evidence(
                outOfSampleNetReturn: 0.1234567890123456789012345678m,
                costStressedNetReturn: 0.0234567890123456789012345678m)));

        var outOfSample = Assert.Single(result.Results, gate => gate.GateId == "out-of-sample-holdout");
        var cost = Assert.Single(result.Results, gate => gate.GateId == "cost-liquidity-feasibility");
        Assert.Equal(0.1234567890123456789012345678m, outOfSample.MeasuredValue);
        Assert.Equal(0.0234567890123456789012345678m, cost.MeasuredValue);
        Assert.All(result.Results, gate => Assert.Equal(TimeSpan.Zero, gate.EvaluatedAtUtc.Offset));
        Assert.Throws<ArgumentException>(() => Input(evaluatedAtUtc: s_nowUtc.ToOffset(TimeSpan.FromHours(2))));
    }

    private static StrategyRejectionGateEvaluationInput Input(
        StrategyApproval? approval = null,
        StrategyResearchEvidence? evidence = null,
        DateTimeOffset? evaluatedAtUtc = null,
        bool includeDefaultEvidence = true) =>
        new(
            approval ?? Approval(StrategyApprovalActor.Human(Guid.NewGuid())),
            new StrategyTimeframeConfiguration(
                CandleInterval.OneHour,
                CandleInterval.OneHour,
                CandleInterval.OneHour),
            TradingProductType.Spot,
            StrategyApprovalMode.Backtest,
            includeDefaultEvidence ? evidence ?? Evidence() : evidence,
            evaluatedAtUtc ?? s_nowUtc);

    private static StrategyApproval Approval(StrategyApprovalActor actor) =>
        StrategyApproval.CreateDraft(
            Guid.NewGuid(),
            new StrategyVersion(
                new StrategyTemplateVersionIdentity("platform.test", 1),
                new StrategyParameterSchemaReference("test-parameters", 1, Fingerprint),
                Fingerprint,
                s_nowUtc),
            actor,
            s_nowUtc,
            new StrategyApprovalRequirements(
                [new ApprovedInstrumentScope(AssetClass.Cryptocurrency, s_instrumentId)],
                100,
                100m,
                0.10m,
                0.01m,
                TimeSpan.FromMinutes(30),
                [CandleInterval.OneHour],
                [TradingProductType.Spot],
                [StrategyApprovalMode.Backtest],
                new StrategyTimeframeConfiguration(
                    CandleInterval.OneHour,
                    CandleInterval.OneHour,
                    CandleInterval.OneHour)));

    private static StrategyResearchEvidence Evidence(
        bool? dataQualitySafe = true,
        decimal? outOfSampleNetReturn = 0.02m,
        decimal? costStressedNetReturn = 0.01m,
        DateTimeOffset? asOfUtc = null) =>
        new(
            new ResearchEvidenceProvenance("recorded-research", Fingerprint, asOfUtc ?? s_nowUtc),
            new StrategyApprovalEvidence(
                s_instrumentId,
                AssetClass.Cryptocurrency,
                100,
                100m,
                0.10m,
                0.01m,
                s_nowUtc),
            dataQualitySafe,
            0.03m,
            outOfSampleNetReturn,
            true,
            costStressedNetReturn,
            0.90m,
            0.90m,
            Fingerprint);
}
