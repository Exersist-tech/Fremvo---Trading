using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class RegimeSwitchingEnsembleResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly StrategyTimeframeConfiguration s_timeframes =
        new(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void SelectsOnlyTheImmutableApprovedFamiliesForTheClassifiedRegime()
    {
        var model = new RegimeSwitchingEnsembleResearchModel();
        var result = model.Evaluate(Input(
            MarketRegime.TrendingUp,
            Component("ema-trend-continuation-v1", StrategyAnalysisDirection.Bullish, .50m),
            Component("bollinger-mean-reversion-v1", StrategyAnalysisDirection.Bearish, .50m)));

        Assert.Equal(RegimeSwitchingEnsembleStatus.Observed, result.Status);
        Assert.Single(result.Components);
        Assert.Equal("ema-trend-continuation-v1", result.Components[0].Proposal.TemplateId.Value);
        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
        Assert.Equal(.50m, result.Confidence);
    }

    [Fact]
    public void UnknownOrSpotUnsafeRegimesNeverChooseADefaultFamily()
    {
        var model = new RegimeSwitchingEnsembleResearchModel();
        var unknown = model.Evaluate(Input(MarketRegime.Unknown, Component("ema-trend-continuation-v1", StrategyAnalysisDirection.Bullish, .50m)));
        var down = model.Evaluate(Input(MarketRegime.TrendingDown, Component("ema-trend-continuation-v1", StrategyAnalysisDirection.Bullish, .50m)));

        Assert.Equal(RegimeSwitchingEnsembleStatus.Blocked, unknown.Status);
        Assert.Empty(unknown.Components);
        Assert.Equal(RegimeSwitchingEnsembleStatus.Neutral, down.Status);
        Assert.Empty(down.Components);
        Assert.Equal(StrategyAnalysisDirection.Neutral, down.Direction);
    }

    [Fact]
    public void BlocksMismatchedOrFutureUnsafeProvenance()
    {
        var model = new RegimeSwitchingEnsembleResearchModel();
        var mismatched = Component("donchian-breakout-ensemble-v1", StrategyAnalysisDirection.Bullish, .50m, datasetId: "other");
        var future = Component("rsi-pullback-v1", StrategyAnalysisDirection.Bullish, .50m, asOfUtc: s_asOfUtc.AddMinutes(5));

        var mismatchResult = model.Evaluate(Input(MarketRegime.TrendingUp, Component("ema-trend-continuation-v1", StrategyAnalysisDirection.Bullish, .50m), mismatched));
        var futureResult = model.Evaluate(Input(MarketRegime.TrendingUp, Component("ema-trend-continuation-v1", StrategyAnalysisDirection.Bullish, .50m), future));

        Assert.Equal(RegimeSwitchingEnsembleStatus.Blocked, mismatchResult.Status);
        Assert.Equal(RegimeSwitchingEnsembleStatus.Blocked, futureResult.Status);
        Assert.Contains("provenance", futureResult.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreservesNeutralEvidenceAndReturnsNeutralWhenNoComponentHasDirection()
    {
        var model = new RegimeSwitchingEnsembleResearchModel();
        var neutral = Component("ema-trend-continuation-v1", StrategyAnalysisDirection.Neutral, 0m);
        var result = model.Evaluate(Input(MarketRegime.TrendingUp, neutral));

        Assert.Equal(RegimeSwitchingEnsembleStatus.Neutral, result.Status);
        Assert.Single(result.Components);
        Assert.Same(neutral, result.Components[0]);
        Assert.Equal(0m, result.Confidence);
    }

    [Fact]
    public void IsVersionedDeterministicGateRequiredAndHasNoExecutionOrSizingSurface()
    {
        var model = new RegimeSwitchingEnsembleResearchModel();
        var input = Input(MarketRegime.TrendingUp, Component("ema-trend-continuation-v1", StrategyAnalysisDirection.Bullish, .50m));
        var first = model.Evaluate(input);
        var second = model.Evaluate(input);
        var withoutGates = model.Evaluate(new RegimeSwitchingEnsembleEvaluationInput(input.Classification, input.ComponentObservations, null));

        Assert.Equal(first.MappingHash, second.MappingHash);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.Equal(1, first.EnsembleVersion);
        Assert.Matches("^[A-F0-9]{64}$", first.MappingHash);
        Assert.Equal(input.Classification.ClassifierVersion, first.ClassifierVersion);
        Assert.Equal(RegimeSwitchingEnsembleStatus.Blocked, withoutGates.Status);
        Assert.DoesNotContain(typeof(RegimeSwitchingEnsembleResearchModel).Assembly.GetReferencedAssemblies(), assembly =>
            assembly.Name?.Contains("Execution", StringComparison.OrdinalIgnoreCase) == true
            || assembly.Name?.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase) == true
            || assembly.Name?.Contains("Http", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(typeof(RegimeSwitchingEnsembleResearchResult).GetMembers(BindingFlags.Public | BindingFlags.Instance), member =>
            member.Name.Contains("Quantity", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Sizing", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("Leverage", StringComparison.OrdinalIgnoreCase)
            || typeof(TradeIntent).IsAssignableFrom(member switch
            {
                PropertyInfo property => property.PropertyType,
                MethodInfo method => method.ReturnType,
                _ => typeof(void)
            }));
    }

    private static RegimeSwitchingEnsembleEvaluationInput Input(
        MarketRegime regime,
        params RegimeSwitchingComponentObservation[] components) =>
        new(Classification(regime), components, Gates(s_asOfUtc));

    private static RegimeSwitchingComponentObservation Component(
        string templateId,
        StrategyAnalysisDirection direction,
        decimal confidence,
        string datasetId = "dataset-20260920",
        DateTimeOffset? asOfUtc = null) =>
        new(
            new StrategyAnalysisProposal(new StrategyTemplateId(templateId), asOfUtc ?? s_asOfUtc, direction, confidence, "Recorded component evidence; research only."),
            new RegimeSwitchingObservationProvenance(datasetId, "BTCUSD", s_timeframes, asOfUtc ?? s_asOfUtc, true));

    private static RegimeClassification Classification(MarketRegime regime)
    {
        if (regime == MarketRegime.Unknown)
        {
            return new DeterministicRegimeClassifier().Evaluate(
                new RegimeClassificationInput([], s_asOfUtc, null));
        }

        var closes = regime switch
        {
            MarketRegime.TrendingUp => new[] { 100m, 101m, 102m, 103m, 104m },
            MarketRegime.TrendingDown => new[] { 104m, 103m, 102m, 101m, 100m },
            _ => throw new ArgumentOutOfRangeException(nameof(regime))
        };
        var parameters = new RegimeClassifierParameters(2, 3, 2, 2, 2m, .10m, 50m, 50m, TimeSpan.FromHours(2));
        return new DeterministicRegimeClassifier(parameters).Evaluate(
            new RegimeClassificationInput(Candles(closes), s_asOfUtc, Gates(s_asOfUtc)));
    }

    private static Candle[] Candles(IEnumerable<decimal> closes)
    {
        var values = closes.ToArray();
        return values.Select((close, index) =>
        {
            var closeTime = s_asOfUtc.AddHours(index - values.Length + 1);
            return new Candle("BTCUSD", CandleInterval.OneHour, closeTime.AddHours(-1), closeTime, close - .5m, close + .5m, close - .5m, close, 1m, true, false);
        }).ToArray();
    }

    private static RejectionGateEvaluation Gates(DateTimeOffset asOfUtc)
    {
        var instrument = Guid.Parse("abababab-abab-abab-abab-abababababab");
        var approval = StrategyApproval.CreateDraft(Guid.NewGuid(), new StrategyVersion(new StrategyTemplateVersionIdentity("platform.regime-ensemble", 1), new StrategyParameterSchemaReference("regime-ensemble", 1, Fingerprint), Fingerprint, asOfUtc), StrategyApprovalActor.Human(Guid.NewGuid()), asOfUtc, new StrategyApprovalRequirements([new ApprovedInstrumentScope(AssetClass.Cryptocurrency, instrument)], 10, 100m, .10m, .01m, TimeSpan.FromHours(2), [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes], [TradingProductType.Spot], [StrategyApprovalMode.Backtest], s_timeframes));
        var evidence = new StrategyResearchEvidence(new ResearchEvidenceProvenance("recorded-regime-ensemble-evidence", Fingerprint, asOfUtc), new StrategyApprovalEvidence(instrument, AssetClass.Cryptocurrency, 10, 100m, .10m, .01m, asOfUtc), true, .03m, .02m, true, .01m, .90m, .90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(approval, s_timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, asOfUtc));
    }
}
