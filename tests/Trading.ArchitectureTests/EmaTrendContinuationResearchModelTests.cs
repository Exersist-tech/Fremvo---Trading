using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class EmaTrendContinuationResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid s_instrumentId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ProducesBullishResearchObservationForAlignedCompletedTrendContinuation()
    {
        var model = new EmaTrendContinuationResearchModel();
        var input = Input(model, RegimeUptrend(), SignalContinuation(), ExecutionConfirmation());

        var result = model.Evaluate(new EmaTrendContinuationEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
        Assert.Equal(0.60m, result.Confidence);
        Assert.Equal(s_asOfUtc, result.ObservedAtUtc);
        Assert.Contains("analysis only", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturnsNoConditionForSidewaysOrDowntrendRegime(bool downtrend)
    {
        var model = new EmaTrendContinuationResearchModel();
        var regime = downtrend
            ? Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 55).Select(index => 200m - index))
            : Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Repeat(100m, 55));
        var input = Input(model, regime, SignalContinuation(), ExecutionConfirmation());

        var result = model.Evaluate(new EmaTrendContinuationEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("higher-timeframe", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsUnavailableForInsufficientCompletedWarmup()
    {
        var model = new EmaTrendContinuationResearchModel();
        var input = Input(
            model,
            Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 50).Select(index => 100m + index)),
            SignalContinuation(),
            ExecutionConfirmation());

        var result = model.Evaluate(new EmaTrendContinuationEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("Unavailable", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsFutureAlignedSeriesBeforeResearchEvaluation()
    {
        var model = new EmaTrendContinuationResearchModel();
        var configuration = new StrategyTimeframeConfiguration(
            CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var futureSignal = Series(
            StrategyTimeframeRole.Signal,
            CandleInterval.FifteenMinutes,
            Enumerable.Range(0, 30).Select(index => 100m + index),
            s_asOfUtc.AddMinutes(15));

        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(
            model.TemplateId,
            Parameters(model),
            new StrategyState(model.TemplateId, 0, s_asOfUtc.AddHours(-1)),
            configuration,
            [RegimeUptrend(), futureSignal, ExecutionConfirmation()]));
    }

    [Fact]
    public void RejectsUnsafeBarBeforeResearchEvaluation()
    {
        var model = new EmaTrendContinuationResearchModel();
        var unsafeExecution = new Candle(
            "BTCUSD", CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-5), s_asOfUtc,
            100m, 102m, 99m, 101m, 1m, true, false, [DataQualityIssue.Stale]);

        Assert.Throws<ArgumentException>(() => new StrategyTimeframeSeries(
            StrategyTimeframeRole.Execution,
            CandleInterval.FiveMinutes,
            [CandleAt(CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-10), s_asOfUtc.AddMinutes(-5), 100m), unsafeExecution]));
    }

    [Fact]
    public void RejectsUnsafeParameterBoundsAndInvertedEmaPeriods()
    {
        var model = new EmaTrendContinuationResearchModel();
        var values = Values(regimeFast: 30m, regimeSlow: 30m);
        Assert.Throws<ArgumentException>(() => model.Evaluate(Input(
            model, RegimeUptrend(), SignalContinuation(), ExecutionConfirmation(),
            new StrategyParameterSet(model.ParameterDefinitions, values))));

        values[RegimeFastPeriod] = StrategyParameterValue.WholeNumber(31m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(model.ParameterDefinitions, values));
    }

    [Fact]
    public void MissingOrFailedGatesBlockCondition()
    {
        var model = new EmaTrendContinuationResearchModel();
        var input = Input(model, RegimeUptrend(), SignalContinuation(), ExecutionConfirmation());
        var missing = model.Evaluate(new EmaTrendContinuationEvaluationInput(input, null));
        var failed = model.Evaluate(new EmaTrendContinuationEvaluationInput(input, AcceptedGates(input, dataQualitySafe: false)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, missing.Direction);
        Assert.Equal(StrategyAnalysisDirection.Neutral, failed.Direction);
        Assert.Contains("rejection gates", failed.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsDeterministicAndDoesNotExposeTradeIntentOrExecutionSurface()
    {
        var model = new EmaTrendContinuationResearchModel();
        var input = Input(model, RegimeUptrend(), SignalContinuation(), ExecutionConfirmation());
        var evaluation = new EmaTrendContinuationEvaluationInput(input, AcceptedGates(input));

        var first = model.Evaluate(evaluation);
        var second = model.Evaluate(evaluation);

        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.DoesNotContain(
            typeof(EmaTrendContinuationResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member switch
            {
                PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
                MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType)
                    || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
                _ => false
            });
    }

    private const string RegimeFastPeriod = "regimeFastPeriod";

    private static StrategyEvaluationInput Input(
        EmaTrendContinuationResearchModel model,
        StrategyTimeframeSeries regime,
        StrategyTimeframeSeries signal,
        StrategyTimeframeSeries execution,
        StrategyParameterSet? parameters = null) =>
        new(
            model.TemplateId,
            parameters ?? Parameters(model),
            new StrategyState(model.TemplateId, 0, s_asOfUtc.AddHours(-1)),
            new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes),
            [regime, signal, execution]);

    private static StrategyParameterSet Parameters(EmaTrendContinuationResearchModel model) =>
        new(model.ParameterDefinitions, Values());

    private static Dictionary<string, StrategyParameterValue> Values(decimal regimeFast = 20m, decimal regimeSlow = 50m) =>
        new()
        {
            [RegimeFastPeriod] = StrategyParameterValue.WholeNumber(regimeFast),
            ["regimeSlowPeriod"] = StrategyParameterValue.WholeNumber(regimeSlow),
            ["signalFastPeriod"] = StrategyParameterValue.WholeNumber(10m),
            ["signalSlowPeriod"] = StrategyParameterValue.WholeNumber(25m),
            ["confirmationCandles"] = StrategyParameterValue.WholeNumber(2m),
            ["retracementPercent"] = StrategyParameterValue.FromNumeric(1m)
        };

    private static StrategyTimeframeSeries RegimeUptrend() =>
        Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 55).Select(index => 100m + index));

    private static StrategyTimeframeSeries SignalContinuation() =>
        Series(
            StrategyTimeframeRole.Signal,
            CandleInterval.FifteenMinutes,
            [.. Enumerable.Range(0, 27).Select(index => 100m + index), 119m, 128m, 130m]);

    private static StrategyTimeframeSeries ExecutionConfirmation() =>
        Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [100m, 101m]);

    private static StrategyTimeframeSeries Series(
        StrategyTimeframeRole role,
        CandleInterval interval,
        IEnumerable<decimal> closes,
        DateTimeOffset? asOfUtc = null)
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
        var candles = values.Select((close, index) =>
        {
            var closeTime = end - (span * (values.Length - index - 1));
            return CandleAt(interval, closeTime - span, closeTime, close);
        }).ToArray();
        return new StrategyTimeframeSeries(role, interval, candles);
    }

    private static Candle CandleAt(CandleInterval interval, DateTimeOffset open, DateTimeOffset close, decimal price) =>
        new("BTCUSD", interval, open, close, price - 1m, price + 1m, price - 1m, price, 1m, true, false);

    private static RejectionGateEvaluation AcceptedGates(StrategyEvaluationInput input, bool? dataQualitySafe = true)
    {
        var human = StrategyApprovalActor.Human(Guid.NewGuid());
        var approval = StrategyApproval.CreateDraft(
            Guid.NewGuid(),
            new StrategyVersion(
                new StrategyTemplateVersionIdentity("platform.ema-trend-continuation", 1),
                new StrategyParameterSchemaReference("ema-trend-parameters", 1, Fingerprint),
                Fingerprint,
                s_asOfUtc),
            human,
            s_asOfUtc,
            new StrategyApprovalRequirements(
                [new ApprovedInstrumentScope(AssetClass.Cryptocurrency, s_instrumentId)],
                100, 100m, 0.10m, 0.01m, TimeSpan.FromMinutes(30),
                [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes],
                [TradingProductType.Spot],
                [StrategyApprovalMode.Backtest],
                input.Timeframes));
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("recorded-ema-research", Fingerprint, s_asOfUtc),
            new StrategyApprovalEvidence(s_instrumentId, AssetClass.Cryptocurrency, 100, 100m, 0.10m, 0.01m, s_asOfUtc),
            dataQualitySafe, 0.03m, 0.02m, true, 0.01m, 0.90m, 0.90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(
            approval, input.Timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, s_asOfUtc));
    }
}
