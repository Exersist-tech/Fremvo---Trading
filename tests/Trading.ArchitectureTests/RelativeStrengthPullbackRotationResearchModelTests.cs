using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class RelativeStrengthPullbackRotationResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ReturnsHighestRelativeStrengthMemberWithBoundedOwnHistoryPullback()
    {
        var model = new RelativeStrengthPullbackRotationResearchModel();
        var result = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 110m, 120m, 114m]),
            Member("BRAVO", [100m, 104m, 108m, 107m]),
            Member("CHARLIE", [100m, 98m, 97m, 96m])));

        Assert.Equal(RelativeStrengthPullbackResearchStatus.Candidate, result.Status);
        Assert.NotNull(result.Candidate);
        Assert.Equal("ALPHA", result.Candidate.Symbol);
        Assert.Equal(1, result.Candidate.Rank);
        Assert.Equal(0.14m, result.Candidate.RelativeStrengthMomentum);
        Assert.Equal(0.05m, result.Candidate.PullbackFraction);
        Assert.Contains("research only", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsExplainableNeutralWhenNoMemberHasBoundedPullback()
    {
        var model = new RelativeStrengthPullbackRotationResearchModel();
        var result = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 110m, 115m, 120m]),
            Member("BRAVO", [100m, 102m, 105m, 108m])));

        Assert.Equal(RelativeStrengthPullbackResearchStatus.Neutral, result.Status);
        Assert.Null(result.Candidate);
        Assert.Contains("No candidate", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesOrdinalSymbolTieBreakForEqualRelativeStrength()
    {
        var model = new RelativeStrengthPullbackRotationResearchModel();
        var result = model.Evaluate(Input(model,
            Member("ZULU", [100m, 110m, 120m, 110m]),
            Member("ALPHA", [100m, 110m, 120m, 110m])));

        Assert.Equal("ALPHA", result.Candidate!.Symbol);
        Assert.Equal(1, result.Candidate.Rank);
    }

    [Theory]
    [InlineData(InstrumentTradingStatus.Delisted)]
    [InlineData(InstrumentTradingStatus.Unknown)]
    public void MissingOrDelistedRequiredMemberBlocksInsteadOfBeingRemoved(InstrumentTradingStatus status)
    {
        var model = new RelativeStrengthPullbackRotationResearchModel();
        var missing = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 110m, 120m, 114m]),
            Member("MISSING", (decimal[]?)null)));
        var delisted = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 110m, 120m, 114m]),
            Member("GONE", [100m, 110m, 120m, 114m], status: status)));

        Assert.Equal(RelativeStrengthPullbackResearchStatus.Blocked, missing.Status);
        Assert.Contains("MISSING", missing.Rationale, StringComparison.Ordinal);
        Assert.Equal(RelativeStrengthPullbackResearchStatus.Blocked, delisted.Status);
        Assert.Contains("GONE", delisted.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void BlocksUnsafeFutureAndMismatchedHistory()
    {
        var model = new RelativeStrengthPullbackRotationResearchModel();
        var unsafeResult = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 110m, 120m, 114m]),
            Member("UNSAFE", [Candle("UNSAFE", 100m, 0, [DataQualityIssue.Stale]), Candle("UNSAFE", 110m, 1), Candle("UNSAFE", 120m, 2), Candle("UNSAFE", 114m, 3)])));
        var futureResult = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 110m, 120m, 114m]),
            Member("FUTURE", [Candle("FUTURE", 100m, 0), Candle("FUTURE", 110m, 1), Candle("FUTURE", 120m, 2), Candle("FUTURE", 114m, 4)])));
        var mismatchResult = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 110m, 120m, 114m]),
            Member("MISMATCH", [Candle("OTHER", 100m, 0), Candle("OTHER", 110m, 1), Candle("OTHER", 120m, 2), Candle("OTHER", 114m, 3)])));

        Assert.Equal(RelativeStrengthPullbackResearchStatus.Blocked, unsafeResult.Status);
        Assert.Equal(RelativeStrengthPullbackResearchStatus.Blocked, futureResult.Status);
        Assert.Contains("future", futureResult.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RelativeStrengthPullbackResearchStatus.Blocked, mismatchResult.Status);
    }

    [Fact]
    public void RequiresPlatformBoundedParametersAndAcceptedGatesAndIsDeterministic()
    {
        var model = new RelativeStrengthPullbackRotationResearchModel();
        Assert.Throws<ArgumentOutOfRangeException>(() => Parameters(model, minimumPullback: 0.0001m));
        var dataset = Dataset(
            Member("ALPHA", [100m, 110m, 120m, 114m]),
            Member("BRAVO", [100m, 104m, 108m, 107m]));
        var inverted = model.Evaluate(new RelativeStrengthPullbackRotationEvaluationInput(
            dataset, Parameters(model, minimumPullback: 0.11m, maximumPullback: 0.10m), AcceptedGates()));
        var gated = model.Evaluate(new RelativeStrengthPullbackRotationEvaluationInput(dataset, Parameters(model), null));
        var input = new RelativeStrengthPullbackRotationEvaluationInput(dataset, Parameters(model), AcceptedGates());

        Assert.Equal(RelativeStrengthPullbackResearchStatus.Blocked, inverted.Status);
        Assert.Equal(RelativeStrengthPullbackResearchStatus.Blocked, gated.Status);
        Assert.Equal(model.Evaluate(input).Candidate, model.Evaluate(input).Candidate);
    }

    [Fact]
    public void HasNoExecutionOrTradeIntentSurface()
    {
        Assert.DoesNotContain(
            typeof(RelativeStrengthPullbackRotationResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member switch
            {
                PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
                MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType)
                    || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
                _ => false
            });
    }

    private static RelativeStrengthPullbackRotationEvaluationInput Input(
        RelativeStrengthPullbackRotationResearchModel model,
        CrossSectionalMomentumUniverseMember first,
        CrossSectionalMomentumUniverseMember second,
        CrossSectionalMomentumUniverseMember? third = null) =>
        new(Dataset(third is null ? [first, second] : [first, second, third]), Parameters(model), AcceptedGates());

    private static CrossSectionalMomentumDataset Dataset(params CrossSectionalMomentumUniverseMember[] members) =>
        new("relative-strength-known-universe-v1", s_asOfUtc, CandleInterval.OneHour, members);

    private static CrossSectionalMomentumUniverseMember Member(
        string symbol,
        decimal[]? closes,
        InstrumentTradingStatus status = InstrumentTradingStatus.Trading) =>
        new(Guid.NewGuid(), symbol, true, status, s_asOfUtc,
            closes?.Select((close, index) => Candle(symbol, close, index)).ToArray());

    private static CrossSectionalMomentumUniverseMember Member(string symbol, Candle[] candles) =>
        new(Guid.NewGuid(), symbol, true, InstrumentTradingStatus.Trading, s_asOfUtc, candles);

    private static Candle Candle(string symbol, decimal close, int index, IReadOnlyCollection<DataQualityIssue>? issues = null) =>
        new(symbol, CandleInterval.OneHour, s_asOfUtc.AddHours(index - 4), s_asOfUtc.AddHours(index - 3),
            close, close, close, close, 1m, true, false, issues);

    private static StrategyParameterSet Parameters(
        RelativeStrengthPullbackRotationResearchModel model,
        decimal momentumLookback = 3m,
        decimal pullbackLookback = 3m,
        decimal minimumPullback = 0.03m,
        decimal maximumPullback = 0.10m) =>
        new(model.ParameterDefinitions, new Dictionary<string, StrategyParameterValue>
        {
            ["momentumLookbackPeriods"] = StrategyParameterValue.WholeNumber(momentumLookback),
            ["pullbackLookbackPeriods"] = StrategyParameterValue.WholeNumber(pullbackLookback),
            ["minimumPullbackFraction"] = StrategyParameterValue.FromNumeric(minimumPullback),
            ["maximumPullbackFraction"] = StrategyParameterValue.FromNumeric(maximumPullback)
        });

    private static RejectionGateEvaluation AcceptedGates()
    {
        var approval = StrategyApproval.CreateDraft(
            Guid.NewGuid(),
            new StrategyVersion(
                new StrategyTemplateVersionIdentity("platform.relative-strength-pullback-rotation", 1),
                new StrategyParameterSchemaReference("relative-strength-pullback-parameters", 1, Fingerprint),
                Fingerprint, s_asOfUtc),
            StrategyApprovalActor.Human(Guid.NewGuid()), s_asOfUtc,
            new StrategyApprovalRequirements(
                [new ApprovedInstrumentScope(AssetClass.Cryptocurrency, null)],
                4, 100m, 0.10m, 0.01m, TimeSpan.FromMinutes(30),
                [CandleInterval.OneHour], [TradingProductType.Spot], [StrategyApprovalMode.Research],
                new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.OneHour, CandleInterval.OneHour)));
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("recorded-relative-strength-research", Fingerprint, s_asOfUtc),
            new StrategyApprovalEvidence(Guid.NewGuid(), AssetClass.Cryptocurrency, 4, 100m, 0.10m, 0.01m, s_asOfUtc),
            true, 0.03m, 0.02m, true, 0.01m, 0.90m, 0.90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(
            approval,
            new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.OneHour, CandleInterval.OneHour),
            TradingProductType.Spot, StrategyApprovalMode.Research, evidence, s_asOfUtc));
    }
}
