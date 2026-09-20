using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class DeterministicRegimeClassifierTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Theory]
    [MemberData(nameof(RegimeFixtures))]
    public void ClassifiesKnownClosedFixtures(MarketRegime expected, decimal[] closes)
    {
        var model = Model();
        var result = model.Evaluate(Input(Candles(closes)));

        Assert.Equal(expected, result.Regime);
        Assert.NotNull(result.FastEma);
        Assert.NotNull(result.AtrPercent);
        Assert.Contains(":", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifiesHighVolatilityAndUsesInclusiveThresholdBoundaries()
    {
        var parameters = new RegimeClassifierParameters(2, 3, 2, 2, 2m, 0.01m, 1m, 100m, TimeSpan.FromHours(2));
        var model = new DeterministicRegimeClassifier(parameters);
        var volatileResult = model.Evaluate(Input(Candles([100m, 120m, 100m])));
        Assert.Equal(MarketRegime.HighVolatility, volatileResult.Regime);

        var boundaryParameters = new RegimeClassifierParameters(2, 3, 2, 2, 2m, 0.01m, 100m, 100m, TimeSpan.FromHours(2));
        var boundaryModel = new DeterministicRegimeClassifier(boundaryParameters);
        var candles = Candles([100m, 100m, 101m]);
        var preliminary = boundaryModel.Evaluate(Input(candles));
        var exact = new DeterministicRegimeClassifier(new RegimeClassifierParameters(2, 3, 2, 2, 2m, 0.01m, preliminary.AtrPercent!.Value, 100m, TimeSpan.FromHours(2)))
            .Evaluate(Input(candles));
        Assert.Equal(MarketRegime.HighVolatility, exact.Regime);
    }

    [Fact]
    public void FailsClosedForWarmupAndUnsafeFutureReversedMixedStaleOrMisalignedData()
    {
        var model = Model();
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(Input(Candles([100m, 101m]))).Regime);
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(Input(Candles(Enumerable.Range(0, 10).Select(index => 100m + index), unsafeIndex: 4))).Regime);
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(Input(Candles(Enumerable.Range(0, 10).Select(index => 100m + index), end: s_asOfUtc.AddHours(1)))).Regime);

        var reversed = Candles(Enumerable.Range(0, 10).Select(index => 100m + index)).Reverse().ToArray();
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(Input(reversed)).Regime);

        var mixed = Candles(Enumerable.Range(0, 10).Select(index => 100m + index));
        mixed[4] = Candle("ETHUSD", mixed[4].OpenTimeUtc, mixed[4].CloseTimeUtc, 104m);
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(Input(mixed)).Regime);

        var stale = Candles(Enumerable.Range(0, 10).Select(index => 100m + index), end: s_asOfUtc.AddHours(-3));
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(Input(stale)).Regime);

        var misaligned = Candles(Enumerable.Range(0, 10).Select(index => 100m + index));
        misaligned[5] = Candle("BTCUSD", misaligned[5].OpenTimeUtc.AddMinutes(1), misaligned[5].CloseTimeUtc.AddMinutes(1), 105m);
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(Input(misaligned)).Regime);
    }

    [Fact]
    public void BlocksMissingFailedOrAsOfMisalignedGates()
    {
        var model = Model();
        var candles = Candles(Enumerable.Range(0, 10).Select(index => 100m + index));
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(new RegimeClassificationInput(candles, s_asOfUtc, null)).Regime);
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(new RegimeClassificationInput(candles, s_asOfUtc, Gates(s_asOfUtc, false))).Regime);
        Assert.Equal(MarketRegime.Unknown, model.Evaluate(new RegimeClassificationInput(candles, s_asOfUtc, Gates(s_asOfUtc.AddMinutes(-5), true))).Regime);
    }

    [Fact]
    public void HasImmutableVersionedIdentityParametersAndDeterministicResultWithoutExecutionDependencies()
    {
        var model = Model();
        var input = Input(Candles(Enumerable.Range(0, 10).Select(index => 100m + index)));
        var first = model.Evaluate(input);
        var second = model.Evaluate(input);

        Assert.Equal("platform.deterministic-regime", first.ClassifierId);
        Assert.Equal(1, first.ClassifierVersion);
        Assert.Matches("^[A-F0-9]{64}$", first.ClassifierHash);
        Assert.Same(model.Parameters, first.Parameters);
        Assert.Equal(first.Regime, second.Regime);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.Equal(first.AtrPercent, second.AtrPercent);
        Assert.DoesNotContain(typeof(DeterministicRegimeClassifier).Assembly.GetReferencedAssemblies(), assembly =>
            assembly.Name?.Contains("Execution", StringComparison.OrdinalIgnoreCase) == true
            || assembly.Name?.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase) == true
            || assembly.Name?.Contains("Http", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(typeof(DeterministicRegimeClassifier).GetMembers(BindingFlags.Public | BindingFlags.Instance), member => member switch
        {
            PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
            MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType) || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
            _ => false
        });
    }

    private static DeterministicRegimeClassifier Model() =>
        new(new RegimeClassifierParameters(2, 3, 2, 2, 2m, 0.40m, 50m, 50m, TimeSpan.FromHours(2)));

    public static IEnumerable<object[]> RegimeFixtures =>
    [
        [MarketRegime.TrendingUp, new decimal[] { 100m, 101m, 102m, 103m, 104m, 105m, 106m, 107m, 108m, 109m }],
        [MarketRegime.TrendingDown, new decimal[] { 109m, 108m, 107m, 106m, 105m, 104m, 103m, 102m, 101m, 100m }],
        [MarketRegime.Ranging, new decimal[] { 100m, 101m, 100m, 101m, 100m, 101m, 100m, 101m, 100m, 101m }]
    ];

    private static RegimeClassificationInput Input(IReadOnlyList<Candle> candles) =>
        new(candles, s_asOfUtc, Gates(s_asOfUtc, true));

    private static Candle[] Candles(IEnumerable<decimal> closes, int? unsafeIndex = null, DateTimeOffset? end = null)
    {
        var values = closes.ToArray();
        var close = end ?? s_asOfUtc;
        return values.Select((value, index) =>
        {
            var open = close.AddHours(index - values.Length);
            return Candle("BTCUSD", open, open.AddHours(1), value, unsafeIndex != index);
        }).ToArray();
    }

    private static Candle Candle(string symbol, DateTimeOffset open, DateTimeOffset close, decimal value, bool safe = true) =>
        new(symbol, CandleInterval.OneHour, open, close, value - 0.5m, value + 0.5m, value - 0.5m, value, 1m, true, false, safe ? [] : [DataQualityIssue.Stale]);

    private static RejectionGateEvaluation Gates(DateTimeOffset asOfUtc, bool quality)
    {
        var instrument = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var configuration = new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var approval = StrategyApproval.CreateDraft(Guid.NewGuid(), new StrategyVersion(new StrategyTemplateVersionIdentity("platform.regime", 1), new StrategyParameterSchemaReference("regime", 1, Fingerprint), Fingerprint, asOfUtc), StrategyApprovalActor.Human(Guid.NewGuid()), asOfUtc, new StrategyApprovalRequirements([new ApprovedInstrumentScope(AssetClass.Cryptocurrency, instrument)], 10, 100m, .10m, .01m, TimeSpan.FromHours(2), [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes], [TradingProductType.Spot], [StrategyApprovalMode.Backtest], configuration));
        var evidence = new StrategyResearchEvidence(new ResearchEvidenceProvenance("recorded-regime-evidence", Fingerprint, asOfUtc), new StrategyApprovalEvidence(instrument, AssetClass.Cryptocurrency, 10, 100m, .10m, .01m, asOfUtc), quality, .03m, .02m, true, .01m, .90m, .90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(approval, configuration, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, asOfUtc));
    }
}
