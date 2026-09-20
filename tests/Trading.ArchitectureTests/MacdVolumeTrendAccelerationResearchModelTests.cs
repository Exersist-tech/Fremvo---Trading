using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class MacdVolumeTrendAccelerationResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ProducesBullishObservationForCompletedCrossoverAndVolumeConfirmation()
    {
        var model = new MacdVolumeTrendAccelerationResearchModel();
        var input = Input(model, Signal([.. Enumerable.Repeat(100m, 44), 110m], 200m));

        var result = model.Evaluate(new MacdVolumeTrendAccelerationEvaluationInput(input, Gates(input)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
        Assert.Equal(0.50m, result.Confidence);
        Assert.Contains("analysis only", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsNoConditionWithoutCrossoverOrVolumeConfirmation()
    {
        var model = new MacdVolumeTrendAccelerationResearchModel();
        var noCrossover = Input(model, Signal(Enumerable.Repeat(100m, 45), 200m));
        var noVolume = Input(model, Signal([.. Enumerable.Repeat(100m, 44), 110m], 1m));

        Assert.All(new[] { model.Evaluate(new MacdVolumeTrendAccelerationEvaluationInput(noCrossover, Gates(noCrossover))), model.Evaluate(new MacdVolumeTrendAccelerationEvaluationInput(noVolume, Gates(noVolume))) }, result =>
        {
            Assert.Equal(StrategyAnalysisDirection.Neutral, result.Direction);
            Assert.Contains("No condition", result.Rationale, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AcceptsVolumeAtInclusiveMultiplierBoundary()
    {
        var model = new MacdVolumeTrendAccelerationResearchModel();
        var input = Input(model, Signal([.. Enumerable.Repeat(100m, 44), 110m], 1.5m));

        Assert.Equal(StrategyAnalysisDirection.Bullish, model.Evaluate(new MacdVolumeTrendAccelerationEvaluationInput(input, Gates(input))).Direction);
    }

    [Fact]
    public void ReturnsUnavailableForWarmupAndBlocksMissingOrFailedGates()
    {
        var model = new MacdVolumeTrendAccelerationResearchModel();
        var warmup = Input(model, Signal(Enumerable.Repeat(100m, 34), 2m));
        var ready = Input(model, Signal([.. Enumerable.Repeat(100m, 44), 110m], 200m));

        Assert.Contains("Unavailable", model.Evaluate(new MacdVolumeTrendAccelerationEvaluationInput(warmup, Gates(warmup))).Rationale, StringComparison.Ordinal);
        Assert.Contains("rejection gates", model.Evaluate(new MacdVolumeTrendAccelerationEvaluationInput(ready, null)).Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rejection gates", model.Evaluate(new MacdVolumeTrendAccelerationEvaluationInput(ready, Gates(ready, false))).Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnsafeFutureAndInvalidPlatformParameters()
    {
        var model = new MacdVolumeTrendAccelerationResearchModel();
        var configuration = new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var future = Signal(Enumerable.Repeat(100m, 45), 2m, s_asOfUtc.AddMinutes(15));
        var unsafeCandle = Candle(CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-5), s_asOfUtc, 100m, 1m, false);
        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [Regime(), future, Execution()]));
        Assert.Throws<ArgumentException>(() => new StrategyTimeframeSeries(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [unsafeCandle]));

        var values = Values(); values["volumeMultiplier"] = StrategyParameterValue.FromNumeric(10.01m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(model.ParameterDefinitions, values));
        Assert.Throws<ArgumentException>(() => model.Evaluate(Input(model, Signal(Enumerable.Repeat(100m, 45), 2m), Parameters(model, fast: 26m, slow: 26m))));
    }

    [Fact]
    public void IsDeterministicAndHasNoTradeIntentOrExecutionSurface()
    {
        var model = new MacdVolumeTrendAccelerationResearchModel();
        var input = Input(model, Signal([.. Enumerable.Repeat(100m, 44), 110m], 200m));
        var evaluation = new MacdVolumeTrendAccelerationEvaluationInput(input, Gates(input));
        var first = model.Evaluate(evaluation);
        var second = model.Evaluate(evaluation);
        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.DoesNotContain(typeof(MacdVolumeTrendAccelerationResearchModel).Assembly.GetReferencedAssemblies(), a => a.Name?.Contains("Execution", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(typeof(MacdVolumeTrendAccelerationResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance), member => member is PropertyInfo p && typeof(TradeIntent).IsAssignableFrom(p.PropertyType));
    }

    private static StrategyEvaluationInput Input(MacdVolumeTrendAccelerationResearchModel model, StrategyTimeframeSeries signal, StrategyParameterSet? parameters = null) =>
        new(model.TemplateId, parameters ?? Parameters(model), State(model), new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes), [Regime(), signal, Execution()]);
    private static StrategyState State(MacdVolumeTrendAccelerationResearchModel model) => new(model.TemplateId, 0, s_asOfUtc.AddHours(-1));
    private static StrategyParameterSet Parameters(MacdVolumeTrendAccelerationResearchModel model, decimal fast = 12m, decimal slow = 26m) => new(model.ParameterDefinitions, Values(fast, slow));
    private static Dictionary<string, StrategyParameterValue> Values(decimal fast = 12m, decimal slow = 26m) => new()
    {
        ["fastPeriod"] = StrategyParameterValue.WholeNumber(fast), ["slowPeriod"] = StrategyParameterValue.WholeNumber(slow),
        ["signalPeriod"] = StrategyParameterValue.WholeNumber(9m), ["volumeLookback"] = StrategyParameterValue.WholeNumber(20m),
        ["volumeMultiplier"] = StrategyParameterValue.FromNumeric(1.5m)
    };
    private static StrategyTimeframeSeries Regime() => Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, Enumerable.Repeat(100m, 2), 1m);
    private static StrategyTimeframeSeries Execution() => Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [100m], 1m);
    private static StrategyTimeframeSeries Signal(IEnumerable<decimal> closes, decimal lastVolume, DateTimeOffset? end = null) =>
        Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, closes, lastVolume, end);
    private static StrategyTimeframeSeries Series(StrategyTimeframeRole role, CandleInterval interval, IEnumerable<decimal> closes, decimal lastVolume, DateTimeOffset? end = null)
    {
        var values = closes.ToArray(); var closeAt = end ?? s_asOfUtc; var span = interval switch { CandleInterval.OneHour => TimeSpan.FromHours(1), CandleInterval.FifteenMinutes => TimeSpan.FromMinutes(15), _ => TimeSpan.FromMinutes(5) };
        return new StrategyTimeframeSeries(role, interval, values.Select((price, index) => Candle(interval, closeAt - span * (values.Length - index), closeAt - span * (values.Length - index - 1), price, index == values.Length - 1 ? lastVolume : 1m)).ToArray());
    }
    private static Candle Candle(CandleInterval interval, DateTimeOffset open, DateTimeOffset close, decimal price, decimal volume, bool safe = true) =>
        new("BTCUSD", interval, open, close, price - 1m, price + 1m, price - 1m, price, volume, true, false, safe ? [] : [DataQualityIssue.Stale]);
    private static RejectionGateEvaluation Gates(StrategyEvaluationInput input, bool quality = true)
    {
        var approval = StrategyApproval.CreateDraft(Guid.NewGuid(), new StrategyVersion(new StrategyTemplateVersionIdentity("platform.macd-volume", 1), new StrategyParameterSchemaReference("macd-volume-parameters", 1, Fingerprint), Fingerprint, s_asOfUtc), StrategyApprovalActor.Human(Guid.NewGuid()), s_asOfUtc, new StrategyApprovalRequirements([new ApprovedInstrumentScope(AssetClass.Cryptocurrency, Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"))], 100, 100m, .10m, .01m, TimeSpan.FromMinutes(30), [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes], [TradingProductType.Spot], [StrategyApprovalMode.Backtest], input.Timeframes));
        var evidence = new StrategyResearchEvidence(new ResearchEvidenceProvenance("recorded-macd-volume-research", Fingerprint, s_asOfUtc), new StrategyApprovalEvidence(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), AssetClass.Cryptocurrency, 100, 100m, .10m, .01m, s_asOfUtc), quality, .03m, .02m, true, .01m, .90m, .90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(approval, input.Timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, s_asOfUtc));
    }
}
