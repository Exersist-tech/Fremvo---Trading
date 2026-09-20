using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.Indicators;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class BollingerMeanReversionResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid s_instrumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ProducesMeanReversionObservationAtDecimalLowerBoundaryInBoundedRange()
    {
        var model = new BollingerMeanReversionResearchModel();
        var input = Input(model, FlatRegime(), SignalAtLowerBand(), Execution());

        var result = model.Evaluate(new BollingerMeanReversionEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
        Assert.Equal(0.40m, result.Confidence);
        Assert.Equal(s_asOfUtc, result.ObservedAtUtc);
        Assert.Contains("lower boundary", result.Rationale, StringComparison.Ordinal);
        Assert.Contains("analysis only", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesDecimalBollingerBoundariesWithoutFloatingPointConversion()
    {
        var candles = Series(
            StrategyTimeframeRole.Signal,
            CandleInterval.FifteenMinutes,
            [99m, 101m]).ClosedCandles;

        var bands = new BollingerBandsCalculator(2, 2m).Calculate(candles).Value;

        Assert.NotNull(bands);
        Assert.Equal(100m, bands.Value.Middle);
        Assert.Equal(102m, bands.Value.Upper);
        Assert.Equal(98m, bands.Value.Lower);
    }

    [Fact]
    public void BlocksMeanReversionWhenExplicitLocalRangeCheckDetectsTrend()
    {
        var model = new BollingerMeanReversionResearchModel();
        var input = Input(model, Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 21).Select(index => 100m + index)), SignalAtLowerBand(), Execution());

        var result = model.Evaluate(new BollingerMeanReversionEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("ranging check failed", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsNoConditionWhenCompletedCloseIsInsideBollingerBoundaries()
    {
        var model = new BollingerMeanReversionResearchModel();
        var signal = Enumerable.Range(0, 20).Select(index => index == 19 ? 100m : index % 2 == 0 ? 99m : 101m);
        var input = Input(model, FlatRegime(), Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, signal), Execution());

        var result = model.Evaluate(new BollingerMeanReversionEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("inside the Bollinger boundaries", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsUnavailableForIncompleteWarmup()
    {
        var model = new BollingerMeanReversionResearchModel();
        var input = Input(
            model,
            Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Repeat(100m, 20)),
            Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, Enumerable.Repeat(100m, 19)),
            Execution());

        var result = model.Evaluate(new BollingerMeanReversionEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("Unavailable", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnsafeFutureAndMisalignedSeriesBeforeEvaluation()
    {
        var model = new BollingerMeanReversionResearchModel();
        var configuration = new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var futureSignal = Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, SignalAtLowerBandCloses(), s_asOfUtc.AddMinutes(15));
        var unsafeCandle = new Candle("BTCUSD", CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-5), s_asOfUtc, 100m, 101m, 99m, 100m, 1m, true, false, [DataQualityIssue.Stale]);
        var misaligned = Series(StrategyTimeframeRole.Signal, CandleInterval.OneHour, SignalAtLowerBandCloses());

        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [FlatRegime(), futureSignal, Execution()]));
        Assert.Throws<ArgumentException>(() => new StrategyTimeframeSeries(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [CandleAt(CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-10), s_asOfUtc.AddMinutes(-5), 100m), unsafeCandle]));
        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [FlatRegime(), misaligned, Execution()]));
    }

    [Fact]
    public void RejectsOutOfBoundsOrAlteredPlatformParameters()
    {
        var model = new BollingerMeanReversionResearchModel();
        var values = Values();
        values["maximumEmaSlopePercent"] = StrategyParameterValue.FromNumeric(0.51m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(model.ParameterDefinitions, values));

        var alteredDefinitions = model.ParameterDefinitions.Select(definition =>
            definition.Name == "bollingerPeriod"
                ? new StrategyParameterDefinition(definition.Name, definition.Minimum, definition.Maximum, 21m, definition.Description, definition.Required, definition.ValueType)
                : definition).ToArray();
        Assert.Throws<ArgumentException>(() => model.Evaluate(Input(model, FlatRegime(), SignalAtLowerBand(), Execution(), new StrategyParameterSet(alteredDefinitions, Values()))));
    }

    [Fact]
    public void MissingOrFailedGatesBlockEvaluation()
    {
        var model = new BollingerMeanReversionResearchModel();
        var input = Input(model, FlatRegime(), SignalAtLowerBand(), Execution());

        var missing = model.Evaluate(new BollingerMeanReversionEvaluationInput(input, null));
        var failed = model.Evaluate(new BollingerMeanReversionEvaluationInput(input, AcceptedGates(input, dataQualitySafe: false)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, missing.Direction);
        Assert.Equal(StrategyAnalysisDirection.Neutral, failed.Direction);
        Assert.Contains("rejection gates", failed.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsDeterministicAndHasNoTradeIntentOrExecutionDependency()
    {
        var model = new BollingerMeanReversionResearchModel();
        var input = Input(model, FlatRegime(), SignalAtLowerBand(), Execution());
        var evaluation = new BollingerMeanReversionEvaluationInput(input, AcceptedGates(input));

        var first = model.Evaluate(evaluation);
        var second = model.Evaluate(evaluation);

        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.DoesNotContain(
            typeof(BollingerMeanReversionResearchModel).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("Execution", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            typeof(BollingerMeanReversionResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member switch
            {
                PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
                MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType)
                    || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
                _ => false
            });
    }

    private static StrategyEvaluationInput Input(BollingerMeanReversionResearchModel model, StrategyTimeframeSeries regime, StrategyTimeframeSeries signal, StrategyTimeframeSeries execution, StrategyParameterSet? parameters = null) =>
        new(model.TemplateId, parameters ?? Parameters(model), State(model), new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes), [regime, signal, execution]);

    private static StrategyState State(BollingerMeanReversionResearchModel model) => new(model.TemplateId, 0, s_asOfUtc.AddHours(-1));
    private static StrategyParameterSet Parameters(BollingerMeanReversionResearchModel model) => new(model.ParameterDefinitions, Values());
    private static Dictionary<string, StrategyParameterValue> Values() => new()
    {
        ["bollingerPeriod"] = StrategyParameterValue.WholeNumber(20m),
        ["standardDeviationMultiplier"] = StrategyParameterValue.FromNumeric(2m),
        ["rangeEmaPeriod"] = StrategyParameterValue.WholeNumber(10m),
        ["maximumEmaSlopePercent"] = StrategyParameterValue.FromNumeric(0.20m),
        ["maximumBandWidthPercent"] = StrategyParameterValue.FromNumeric(5m)
    };

    private static StrategyTimeframeSeries FlatRegime() => Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Repeat(100m, 21));
    private static StrategyTimeframeSeries SignalAtLowerBand() => Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, SignalAtLowerBandCloses());
    private static IEnumerable<decimal> SignalAtLowerBandCloses() => [.. Enumerable.Repeat(100m, 19), 95m];
    private static StrategyTimeframeSeries Execution() => Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [100m]);

    private static StrategyTimeframeSeries Series(StrategyTimeframeRole role, CandleInterval interval, IEnumerable<decimal> closes, DateTimeOffset? asOfUtc = null)
    {
        var values = closes.ToArray();
        var end = asOfUtc ?? s_asOfUtc;
        var span = interval switch
        {
            CandleInterval.OneHour => TimeSpan.FromHours(1),
            CandleInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
            CandleInterval.FiveMinutes => TimeSpan.FromMinutes(5),
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };
        return new StrategyTimeframeSeries(role, interval, values.Select((close, index) =>
        {
            var closeTime = end - (span * (values.Length - index - 1));
            return CandleAt(interval, closeTime - span, closeTime, close);
        }).ToArray());
    }

    private static Candle CandleAt(CandleInterval interval, DateTimeOffset open, DateTimeOffset close, decimal price) =>
        new("BTCUSD", interval, open, close, price - 1m, price + 1m, price - 1m, price, 1m, true, false);

    private static RejectionGateEvaluation AcceptedGates(StrategyEvaluationInput input, bool? dataQualitySafe = true)
    {
        var human = StrategyApprovalActor.Human(Guid.NewGuid());
        var approval = StrategyApproval.CreateDraft(
            Guid.NewGuid(),
            new StrategyVersion(new StrategyTemplateVersionIdentity("platform.bollinger-mean-reversion", 1), new StrategyParameterSchemaReference("bollinger-mean-reversion-parameters", 1, Fingerprint), Fingerprint, s_asOfUtc),
            human, s_asOfUtc,
            new StrategyApprovalRequirements([new ApprovedInstrumentScope(AssetClass.Cryptocurrency, s_instrumentId)], 100, 100m, 0.10m, 0.01m, TimeSpan.FromMinutes(30), [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes], [TradingProductType.Spot], [StrategyApprovalMode.Backtest], input.Timeframes));
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("recorded-bollinger-research", Fingerprint, s_asOfUtc),
            new StrategyApprovalEvidence(s_instrumentId, AssetClass.Cryptocurrency, 100, 100m, 0.10m, 0.01m, s_asOfUtc),
            dataQualitySafe, 0.03m, 0.02m, true, 0.01m, 0.90m, 0.90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(
            approval, input.Timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, s_asOfUtc));
    }
}
