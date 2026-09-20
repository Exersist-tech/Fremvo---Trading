using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class CrossSectionalMomentumRotationResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void RanksCompleteKnownUniverseByDecimalMomentumDeterministically()
    {
        var model = new CrossSectionalMomentumRotationResearchModel();
        var input = Input(model,
            Member("ALPHA", [100m, 105m, 110m, 120m]),
            Member("BRAVO", [100m, 101m, 102m, 103m]),
            Member("CHARLIE", [100m, 99m, 98m, 97m]));

        var first = model.Evaluate(input);
        var second = model.Evaluate(input);

        Assert.Equal(CrossSectionalMomentumResearchStatus.Ranked, first.Status);
        Assert.Equal(["ALPHA", "BRAVO"], first.Rankings.Select(item => item.Symbol));
        Assert.Equal([1, 2], first.Rankings.Select(item => item.Rank));
        Assert.Equal(0.20m, first.Rankings[0].MomentumReturn);
        Assert.Equal(first.Rankings, second.Rankings);
        Assert.Contains("research observations only", first.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesOrdinalSymbolTieBreak()
    {
        var model = new CrossSectionalMomentumRotationResearchModel();
        var result = model.Evaluate(Input(model,
            Member("ZULU", [100m, 100m, 100m, 110m]),
            Member("ALPHA", [100m, 100m, 100m, 110m])));

        Assert.Equal(["ALPHA", "ZULU"], result.Rankings.Select(item => item.Symbol));
    }

    [Theory]
    [InlineData(InstrumentTradingStatus.Delisted)]
    [InlineData(InstrumentTradingStatus.Unknown)]
    public void DelistedOrUnknownRequiredUniverseMemberBlocksInsteadOfBeingExcluded(InstrumentTradingStatus status)
    {
        var model = new CrossSectionalMomentumRotationResearchModel();
        var result = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 101m, 102m, 103m]),
            Member("GONE", [100m, 101m, 102m, 103m], status: status)));

        Assert.Equal(CrossSectionalMomentumResearchStatus.Blocked, result.Status);
        Assert.Empty(result.Rankings);
        Assert.Contains("GONE", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrUnsafeHistoryForRequiredUniverseMemberBlocksInsteadOfBeingExcluded()
    {
        var model = new CrossSectionalMomentumRotationResearchModel();
        var missing = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 101m, 102m, 103m]),
            Member("MISSING", (decimal[]?)null)));
        var unsafeCandle = Candle("UNSAFE", 100m, 0, [DataQualityIssue.Stale]);
        var unsafeResult = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 101m, 102m, 103m]),
            Member("UNSAFE", [unsafeCandle])));

        Assert.Equal(CrossSectionalMomentumResearchStatus.Blocked, missing.Status);
        Assert.Contains("MISSING", missing.Rationale, StringComparison.Ordinal);
        Assert.Equal(CrossSectionalMomentumResearchStatus.Blocked, unsafeResult.Status);
        Assert.Contains("UNSAFE", unsafeResult.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsFutureCandleAndStaleMembershipEvidence()
    {
        var model = new CrossSectionalMomentumRotationResearchModel();
        var future = Candle("FUTURE", 103m, 4);
        var futureResult = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 101m, 102m, 103m]),
            Member("FUTURE", [Candle("FUTURE", 100m, 0), Candle("FUTURE", 101m, 1), Candle("FUTURE", 102m, 2), future])));
        var staleResult = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 101m, 102m, 103m], evidenceAt: s_asOfUtc.AddHours(-1)),
            Member("BRAVO", [100m, 101m, 102m, 103m])));

        Assert.Equal(CrossSectionalMomentumResearchStatus.Blocked, futureResult.Status);
        Assert.Contains("future", futureResult.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CrossSectionalMomentumResearchStatus.Blocked, staleResult.Status);
        Assert.Contains("stale", staleResult.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnforcesBoundedParametersAndTopCountAgainstCompleteUniverse()
    {
        var model = new CrossSectionalMomentumRotationResearchModel();
        Assert.Throws<ArgumentOutOfRangeException>(() => Parameters(model, lookback: 366m));

        var result = model.Evaluate(Input(model,
            Member("ALPHA", [100m, 101m, 102m, 103m]),
            Member("BRAVO", [100m, 101m, 102m, 103m]),
            parameters: Parameters(model, topCount: 3m)));

        Assert.Equal(CrossSectionalMomentumResearchStatus.Blocked, result.Status);
        Assert.Contains("top count", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingGatesBlockResultAndModelHasNoExecutionSurface()
    {
        var model = new CrossSectionalMomentumRotationResearchModel();
        var dataset = Dataset(Member("ALPHA", [100m, 101m, 102m, 103m]));
        var blocked = model.Evaluate(new CrossSectionalMomentumRotationEvaluationInput(dataset, Parameters(model), null));

        Assert.Equal(CrossSectionalMomentumResearchStatus.Blocked, blocked.Status);
        Assert.Contains("rejection gates", blocked.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(CrossSectionalMomentumRotationResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member switch
            {
                PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
                MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType)
                    || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
                _ => false
            });
    }

    private static CrossSectionalMomentumRotationEvaluationInput Input(
        CrossSectionalMomentumRotationResearchModel model,
        CrossSectionalMomentumUniverseMember first,
        CrossSectionalMomentumUniverseMember second,
        StrategyParameterSet? parameters = null) =>
        new(Dataset(first, second), parameters ?? Parameters(model), AcceptedGates());

    private static CrossSectionalMomentumRotationEvaluationInput Input(
        CrossSectionalMomentumRotationResearchModel model,
        CrossSectionalMomentumUniverseMember first,
        CrossSectionalMomentumUniverseMember second,
        CrossSectionalMomentumUniverseMember third,
        StrategyParameterSet? parameters = null) =>
        new(Dataset(first, second, third), parameters ?? Parameters(model), AcceptedGates());

    private static CrossSectionalMomentumDataset Dataset(params CrossSectionalMomentumUniverseMember[] members) =>
        new("cross-sectional-known-universe-v1", s_asOfUtc, CandleInterval.OneHour, members);

    private static CrossSectionalMomentumUniverseMember Member(
        string symbol,
        decimal[]? closes,
        InstrumentTradingStatus status = InstrumentTradingStatus.Trading,
        DateTimeOffset? evidenceAt = null) =>
        new(
            Guid.NewGuid(),
            symbol,
            true,
            status,
            evidenceAt ?? s_asOfUtc,
            closes?.Select((close, index) => Candle(symbol, close, index)).ToArray());

    private static CrossSectionalMomentumUniverseMember Member(string symbol, Candle[] candles) =>
        new(Guid.NewGuid(), symbol, true, InstrumentTradingStatus.Trading, s_asOfUtc, candles);

    private static Candle Candle(string symbol, decimal close, int index, IReadOnlyCollection<DataQualityIssue>? issues = null) =>
        new(
            symbol,
            CandleInterval.OneHour,
            s_asOfUtc.AddHours(index - 4),
            s_asOfUtc.AddHours(index - 3),
            close,
            close,
            close,
            close,
            1m,
            true,
            false,
            issues);

    private static StrategyParameterSet Parameters(
        CrossSectionalMomentumRotationResearchModel model,
        decimal lookback = 3m,
        decimal topCount = 2m) =>
        new(
            model.ParameterDefinitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["lookbackPeriods"] = StrategyParameterValue.WholeNumber(lookback),
                ["topCount"] = StrategyParameterValue.WholeNumber(topCount)
            });

    private static RejectionGateEvaluation AcceptedGates()
    {
        var approval = StrategyApproval.CreateDraft(
            Guid.NewGuid(),
            new StrategyVersion(
                new StrategyTemplateVersionIdentity("platform.cross-sectional-momentum-rotation", 1),
                new StrategyParameterSchemaReference("cross-sectional-momentum-parameters", 1, Fingerprint),
                Fingerprint,
                s_asOfUtc),
            StrategyApprovalActor.Human(Guid.NewGuid()),
            s_asOfUtc,
            new StrategyApprovalRequirements(
                [new ApprovedInstrumentScope(AssetClass.Cryptocurrency, null)],
                4, 100m, 0.10m, 0.01m, TimeSpan.FromMinutes(30),
                [CandleInterval.OneHour],
                [TradingProductType.Spot],
                [StrategyApprovalMode.Research],
                new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.OneHour, CandleInterval.OneHour)));
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("recorded-cross-sectional-research", Fingerprint, s_asOfUtc),
            new StrategyApprovalEvidence(Guid.NewGuid(), AssetClass.Cryptocurrency, 4, 100m, 0.10m, 0.01m, s_asOfUtc),
            true, 0.03m, 0.02m, true, 0.01m, 0.90m, 0.90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(
            approval,
            new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.OneHour, CandleInterval.OneHour),
            TradingProductType.Spot,
            StrategyApprovalMode.Research,
            evidence,
            s_asOfUtc));
    }
}
