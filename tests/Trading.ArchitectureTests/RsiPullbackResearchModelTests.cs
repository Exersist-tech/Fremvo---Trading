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

public sealed class RsiPullbackResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid s_instrumentId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ProducesBullishResearchObservationForDecimalRsiPullbackInUptrend()
    {
        var model = new RsiPullbackResearchModel();
        var input = Input(model, Uptrend(), PullbackSignal(), Execution());

        var result = model.Evaluate(new RsiPullbackEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
        Assert.Equal(0.50m, result.Confidence);
        Assert.Equal(s_asOfUtc, result.ObservedAtUtc);
        Assert.Contains("signal RSI", result.Rationale, StringComparison.Ordinal);
        Assert.Contains("analysis only", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsesDecimalRsiThresholdAtInclusiveBoundary()
    {
        var model = new RsiPullbackResearchModel();
        var parameters = Parameters(model, maximumPullbackRsi: 50m);
        var signal = Series(
            StrategyTimeframeRole.Signal,
            CandleInterval.FifteenMinutes,
            [100m, 101m, 100m, 101m, 100m, 101m, 100m, 101m, 100m, 101m, 100m, 101m, 100m, 101m, 100m]);
        var input = Input(model, Uptrend(), signal, Execution(), parameters);

        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(signal.ClosedCandles).Value;
        var result = model.Evaluate(new RsiPullbackEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(50m, rsi);
        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
    }

    [Fact]
    public void BlocksDowntrendAndMissingHigherTimeframeTrend()
    {
        var model = new RsiPullbackResearchModel();
        var downtrend = Input(
            model,
            Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 51).Select(index => 200m - index)),
            PullbackSignal(),
            Execution());
        var flat = Input(model, Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Repeat(100m, 51)), PullbackSignal(), Execution());

        var downtrendResult = model.Evaluate(new RsiPullbackEvaluationInput(downtrend, AcceptedGates(downtrend)));
        var flatResult = model.Evaluate(new RsiPullbackEvaluationInput(flat, AcceptedGates(flat)));

        Assert.All(new[] { downtrendResult, flatResult }, result =>
        {
            Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
            Assert.Contains("higher-timeframe", result.Rationale, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ReturnsNoConditionWhenRsiExceedsThreshold()
    {
        var model = new RsiPullbackResearchModel();
        var input = Input(
            model,
            Uptrend(),
            Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, Enumerable.Range(0, 15).Select(index => 100m + index)),
            Execution());

        var result = model.Evaluate(new RsiPullbackEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("exceeds the pullback threshold", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsUnavailableForIncompleteWarmup()
    {
        var model = new RsiPullbackResearchModel();
        var input = Input(
            model,
            Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 50).Select(index => 100m + index)),
            Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, Enumerable.Repeat(100m, 14)),
            Execution());

        var result = model.Evaluate(new RsiPullbackEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("Unavailable", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUnsafeFutureAndMisalignedSeriesBeforeEvaluation()
    {
        var model = new RsiPullbackResearchModel();
        var configuration = new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var futureSignal = Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, PullbackCloses(), s_asOfUtc.AddMinutes(15));
        var unsafeCandle = new Candle("BTCUSD", CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-5), s_asOfUtc, 100m, 101m, 99m, 100m, 1m, true, false, [DataQualityIssue.Stale]);
        var misaligned = Series(StrategyTimeframeRole.Signal, CandleInterval.OneHour, PullbackCloses());

        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [Uptrend(), futureSignal, Execution()]));
        Assert.Throws<ArgumentException>(() => new StrategyTimeframeSeries(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [CandleAt(CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-10), s_asOfUtc.AddMinutes(-5), 100m), unsafeCandle]));
        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [Uptrend(), misaligned, Execution()]));
    }

    [Fact]
    public void RejectsOutOfBoundsAlteredAndInvertedPlatformParameters()
    {
        var model = new RsiPullbackResearchModel();
        var values = Values();
        values["maximumPullbackRsi"] = StrategyParameterValue.FromNumeric(50.01m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(model.ParameterDefinitions, values));

        var inverted = Parameters(model, regimeFast: 30m, regimeSlow: 30m);
        Assert.Throws<ArgumentException>(() => model.Evaluate(Input(model, Uptrend(), PullbackSignal(), Execution(), inverted)));

        var alteredDefinitions = model.ParameterDefinitions.Select(definition =>
            definition.Name == "rsiPeriod"
                ? new StrategyParameterDefinition(definition.Name, definition.Minimum, definition.Maximum, 15m, definition.Description, definition.Required, definition.ValueType)
                : definition).ToArray();
        Assert.Throws<ArgumentException>(() => model.Evaluate(Input(model, Uptrend(), PullbackSignal(), Execution(), new StrategyParameterSet(alteredDefinitions, Values()))));
    }

    [Fact]
    public void MissingOrFailedGatesBlockEvaluation()
    {
        var model = new RsiPullbackResearchModel();
        var input = Input(model, Uptrend(), PullbackSignal(), Execution());

        var missing = model.Evaluate(new RsiPullbackEvaluationInput(input, null));
        var failed = model.Evaluate(new RsiPullbackEvaluationInput(input, AcceptedGates(input, dataQualitySafe: false)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, missing.Direction);
        Assert.Equal(StrategyAnalysisDirection.Neutral, failed.Direction);
        Assert.Contains("rejection gates", failed.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsDeterministicAndHasNoTradeIntentOrExecutionSurface()
    {
        var model = new RsiPullbackResearchModel();
        var input = Input(model, Uptrend(), PullbackSignal(), Execution());
        var evaluation = new RsiPullbackEvaluationInput(input, AcceptedGates(input));

        var first = model.Evaluate(evaluation);
        var second = model.Evaluate(evaluation);

        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.DoesNotContain(
            typeof(RsiPullbackResearchModel).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("Execution", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            typeof(RsiPullbackResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member switch
            {
                PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
                MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType)
                    || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
                _ => false
            });
    }

    private static StrategyEvaluationInput Input(RsiPullbackResearchModel model, StrategyTimeframeSeries regime, StrategyTimeframeSeries signal, StrategyTimeframeSeries execution, StrategyParameterSet? parameters = null) =>
        new(model.TemplateId, parameters ?? Parameters(model), State(model), new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes), [regime, signal, execution]);

    private static StrategyState State(RsiPullbackResearchModel model) => new(model.TemplateId, 0, s_asOfUtc.AddHours(-1));
    private static StrategyParameterSet Parameters(RsiPullbackResearchModel model, decimal regimeFast = 20m, decimal regimeSlow = 50m, decimal maximumPullbackRsi = 35m) =>
        new(model.ParameterDefinitions, Values(regimeFast, regimeSlow, maximumPullbackRsi));
    private static Dictionary<string, StrategyParameterValue> Values(decimal regimeFast = 20m, decimal regimeSlow = 50m, decimal maximumPullbackRsi = 35m) => new()
    {
        ["regimeFastPeriod"] = StrategyParameterValue.WholeNumber(regimeFast),
        ["regimeSlowPeriod"] = StrategyParameterValue.WholeNumber(regimeSlow),
        ["rsiPeriod"] = StrategyParameterValue.WholeNumber(14m),
        ["maximumPullbackRsi"] = StrategyParameterValue.FromNumeric(maximumPullbackRsi)
    };

    private static StrategyTimeframeSeries Uptrend() =>
        Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 51).Select(index => 100m + index));
    private static StrategyTimeframeSeries PullbackSignal() =>
        Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, PullbackCloses());
    private static IEnumerable<decimal> PullbackCloses() => [.. Enumerable.Range(0, 15).Select(index => 115m + index), .. Enumerable.Range(0, 15).Select(index => 129m - index)];
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
            new StrategyVersion(new StrategyTemplateVersionIdentity("platform.rsi-pullback", 1), new StrategyParameterSchemaReference("rsi-pullback-parameters", 1, Fingerprint), Fingerprint, s_asOfUtc),
            human, s_asOfUtc,
            new StrategyApprovalRequirements([new ApprovedInstrumentScope(AssetClass.Cryptocurrency, s_instrumentId)], 100, 100m, 0.10m, 0.01m, TimeSpan.FromMinutes(30), [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes], [TradingProductType.Spot], [StrategyApprovalMode.Backtest], input.Timeframes));
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("recorded-rsi-pullback-research", Fingerprint, s_asOfUtc),
            new StrategyApprovalEvidence(s_instrumentId, AssetClass.Cryptocurrency, 100, 100m, 0.10m, 0.01m, s_asOfUtc),
            dataQualitySafe, 0.03m, 0.02m, true, 0.01m, 0.90m, 0.90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(
            approval, input.Timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, s_asOfUtc));
    }
}
