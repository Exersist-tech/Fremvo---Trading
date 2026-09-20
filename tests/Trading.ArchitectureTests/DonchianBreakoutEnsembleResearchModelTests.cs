using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class DonchianBreakoutEnsembleResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid s_instrumentId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ProducesBullishObservationForBoundedMultiChannelBreakout()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var input = Input(model, Regime(), Signal(150m), Execution());

        var result = model.Evaluate(new DonchianBreakoutEnsembleEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
        Assert.Equal(0.50m, result.Confidence);
        Assert.Equal(s_asOfUtc, result.ObservedAtUtc);
        Assert.Contains("3 of 3", result.Rationale, StringComparison.Ordinal);
        Assert.Contains("analysis only", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsNoConditionWhenOnlyOneChannelConfirms()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var input = Input(model, Regime(), OneConfirmationSignal(), Execution());

        var result = model.Evaluate(new DonchianBreakoutEnsembleEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("1 of 3", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsNoConditionWhenLatestCloseIsInsideChannels()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var input = Input(model, Regime(), Signal(120m), Execution());

        var result = model.Evaluate(new DonchianBreakoutEnsembleEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("No condition", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void DefinesBreakoutStrictlyAbovePriorUpperChannel()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var input = Input(model, Regime(), Signal(140m), Execution());

        var result = model.Evaluate(new DonchianBreakoutEnsembleEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("0 of 3", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsUnavailableForInsufficientCompletedWarmup()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var input = Input(
            model,
            Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 40).Select(index => 100m + index)),
            Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, Enumerable.Range(0, 40).Select(index => 100m + index)),
            Execution());

        var result = model.Evaluate(new DonchianBreakoutEnsembleEvaluationInput(input, AcceptedGates(input)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
        Assert.Contains("Unavailable", result.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsFutureAndUnsafeSeriesBeforeResearchEvaluation()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var configuration = new StrategyTimeframeConfiguration(
            CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var futureSignal = Series(
            StrategyTimeframeRole.Signal,
            CandleInterval.FifteenMinutes,
            Enumerable.Range(0, 41).Select(index => 100m + index),
            s_asOfUtc.AddMinutes(15));
        var unsafeExecution = new Candle(
            "BTCUSD", CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-5), s_asOfUtc,
            100m, 102m, 99m, 101m, 1m, true, false, [DataQualityIssue.Stale]);

        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(
            model.TemplateId, Parameters(model), State(model), configuration, [Regime(), futureSignal, Execution()]));
        Assert.Throws<ArgumentException>(() => new StrategyTimeframeSeries(
            StrategyTimeframeRole.Execution,
            CandleInterval.FiveMinutes,
            [CandleAt(CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-10), s_asOfUtc.AddMinutes(-5), 100m), unsafeExecution]));
    }

    [Fact]
    public void RejectsOutOfBoundsAndNonIncreasingChannelParameters()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var values = Values(shortPeriod: 20m, mediumPeriod: 20m);

        Assert.Throws<ArgumentException>(() => model.Evaluate(Input(
            model, Regime(), Signal(150m), Execution(), new StrategyParameterSet(model.ParameterDefinitions, values))));

        values["longChannelPeriod"] = StrategyParameterValue.WholeNumber(81m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(model.ParameterDefinitions, values));
    }

    [Fact]
    public void MissingOrFailedGatesBlockEvaluation()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var input = Input(model, Regime(), Signal(150m), Execution());

        var missing = model.Evaluate(new DonchianBreakoutEnsembleEvaluationInput(input, null));
        var failed = model.Evaluate(new DonchianBreakoutEnsembleEvaluationInput(input, AcceptedGates(input, dataQualitySafe: false)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, missing.Direction);
        Assert.Equal(StrategyAnalysisDirection.Neutral, failed.Direction);
        Assert.Contains("rejection gates", failed.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsDeterministicAndDoesNotExposeTradeIntentOrExecutionSurface()
    {
        var model = new DonchianBreakoutEnsembleResearchModel();
        var input = Input(model, Regime(), Signal(150m), Execution());
        var evaluation = new DonchianBreakoutEnsembleEvaluationInput(input, AcceptedGates(input));

        var first = model.Evaluate(evaluation);
        var second = model.Evaluate(evaluation);

        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.DoesNotContain(
            typeof(DonchianBreakoutEnsembleResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member switch
            {
                PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
                MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType)
                    || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
                _ => false
            });
    }

    private static StrategyEvaluationInput Input(
        DonchianBreakoutEnsembleResearchModel model,
        StrategyTimeframeSeries regime,
        StrategyTimeframeSeries signal,
        StrategyTimeframeSeries execution,
        StrategyParameterSet? parameters = null) =>
        new(
            model.TemplateId,
            parameters ?? Parameters(model),
            State(model),
            new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes),
            [regime, signal, execution]);

    private static StrategyState State(DonchianBreakoutEnsembleResearchModel model) =>
        new(model.TemplateId, 0, s_asOfUtc.AddHours(-1));

    private static StrategyParameterSet Parameters(DonchianBreakoutEnsembleResearchModel model) =>
        new(model.ParameterDefinitions, Values());

    private static Dictionary<string, StrategyParameterValue> Values(
        decimal shortPeriod = 10m,
        decimal mediumPeriod = 20m,
        decimal longPeriod = 40m,
        decimal confirmations = 2m) =>
        new()
        {
            ["shortChannelPeriod"] = StrategyParameterValue.WholeNumber(shortPeriod),
            ["mediumChannelPeriod"] = StrategyParameterValue.WholeNumber(mediumPeriod),
            ["longChannelPeriod"] = StrategyParameterValue.WholeNumber(longPeriod),
            ["requiredChannelConfirmations"] = StrategyParameterValue.WholeNumber(confirmations)
        };

    private static StrategyTimeframeSeries Regime() =>
        Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Range(0, 41).Select(index => 100m + index));

    private static StrategyTimeframeSeries Signal(decimal latestClose) =>
        Series(
            StrategyTimeframeRole.Signal,
            CandleInterval.FifteenMinutes,
            [.. Enumerable.Range(0, 40).Select(index => 100m + index), latestClose]);

    private static StrategyTimeframeSeries OneConfirmationSignal() =>
        Series(
            StrategyTimeframeRole.Signal,
            CandleInterval.FifteenMinutes,
            [.. Enumerable.Repeat(100m, 20), 140m, .. Enumerable.Repeat(100m, 9), .. Enumerable.Range(0, 10).Select(index => 120m + index), 131m]);

    private static StrategyTimeframeSeries Execution() =>
        Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [100m, 102m]);

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
                new StrategyTemplateVersionIdentity("platform.donchian-breakout-ensemble", 1),
                new StrategyParameterSchemaReference("donchian-breakout-parameters", 1, Fingerprint),
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
            new ResearchEvidenceProvenance("recorded-donchian-research", Fingerprint, s_asOfUtc),
            new StrategyApprovalEvidence(s_instrumentId, AssetClass.Cryptocurrency, 100, 100m, 0.10m, 0.01m, s_asOfUtc),
            dataQualitySafe, 0.03m, 0.02m, true, 0.01m, 0.90m, 0.90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(
            approval, input.Timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, s_asOfUtc));
    }
}
