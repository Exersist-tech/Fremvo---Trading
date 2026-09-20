using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class VolatilityCompressionBreakoutResearchModelTests
{
    private static readonly DateTimeOffset s_asOfUtc = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    [Fact]
    public void ProducesObservationOnlyAfterPriorCompressionAndCurrentClosedBreakout()
    {
        var model = new VolatilityCompressionBreakoutResearchModel();
        var input = Input(model, Signal([.. Enumerable.Repeat(100m, 12), 110m], 2m));

        var result = model.Evaluate(new VolatilityCompressionBreakoutEvaluationInput(input, Gates(input)));

        Assert.Equal(StrategyAnalysisDirection.Bullish, result.Direction);
        Assert.Equal(0.50m, result.Confidence);
        Assert.Contains("3 prior completed candles", result.Rationale, StringComparison.Ordinal);
        Assert.Contains("strictly broke", result.Rationale, StringComparison.Ordinal);
        Assert.Contains("analysis only", result.Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsNoConditionForNoPriorCompressionOrNoCurrentBreakout()
    {
        var model = new VolatilityCompressionBreakoutResearchModel();
        var noCompression = Input(model, Signal([95m, 105m, 95m, 105m, 95m, 105m, 95m, 105m, 95m, 105m, 95m, 105m, 120m], 2m));
        var noBreakout = Input(model, Signal(Enumerable.Repeat(100m, 13), 2m));

        var compressedResult = model.Evaluate(new VolatilityCompressionBreakoutEvaluationInput(noCompression, Gates(noCompression)));
        var breakoutResult = model.Evaluate(new VolatilityCompressionBreakoutEvaluationInput(noBreakout, Gates(noBreakout)));

        Assert.Equal(StrategyAnalysisDirection.Neutral, compressedResult.Direction);
        Assert.Contains("compression window exceeded", compressedResult.Rationale, StringComparison.Ordinal);
        Assert.Equal(StrategyAnalysisDirection.Neutral, breakoutResult.Direction);
        Assert.Contains("did not strictly break", breakoutResult.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsCompressionAndVolumeAtInclusiveBoundaries()
    {
        var model = new VolatilityCompressionBreakoutResearchModel();
        var parameters = Parameters(model, maxWidth: 0m, volumeMultiplier: 2m);
        var input = Input(model, Signal([.. Enumerable.Repeat(100m, 12), 110m], 2m), parameters);

        Assert.Equal(StrategyAnalysisDirection.Bullish, model.Evaluate(new VolatilityCompressionBreakoutEvaluationInput(input, Gates(input))).Direction);
    }

    [Fact]
    public void ReturnsUnavailableForWarmupAndBlocksMissingOrFailedGates()
    {
        var model = new VolatilityCompressionBreakoutResearchModel();
        var warmup = Input(model, Signal(Enumerable.Repeat(100m, 12), 2m));
        var ready = Input(model, Signal([.. Enumerable.Repeat(100m, 12), 110m], 2m));

        Assert.Contains("Unavailable", model.Evaluate(new VolatilityCompressionBreakoutEvaluationInput(warmup, Gates(warmup))).Rationale, StringComparison.Ordinal);
        Assert.Contains("rejection gates", model.Evaluate(new VolatilityCompressionBreakoutEvaluationInput(ready, null)).Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rejection gates", model.Evaluate(new VolatilityCompressionBreakoutEvaluationInput(ready, Gates(ready, false))).Rationale, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnsafeFutureMisalignedAndInvalidParameters()
    {
        var model = new VolatilityCompressionBreakoutResearchModel();
        var configuration = new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var future = Signal([.. Enumerable.Repeat(100m, 12), 110m], 2m, s_asOfUtc.AddMinutes(15));
        var unsafeCandle = Candle(CandleInterval.FiveMinutes, s_asOfUtc.AddMinutes(-5), s_asOfUtc, 100m, 1m, false);
        var misaligned = Series(StrategyTimeframeRole.Signal, CandleInterval.OneHour, Enumerable.Repeat(100m, 13), 1m);
        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [Regime(), future, Execution()]));
        Assert.Throws<ArgumentException>(() => new StrategyTimeframeSeries(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [unsafeCandle]));
        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(model.TemplateId, Parameters(model), State(model), configuration, [Regime(), misaligned, Execution()]));

        var values = Values(); values["maximumBandWidthPercent"] = StrategyParameterValue.FromNumeric(10.01m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(model.ParameterDefinitions, values));
        var altered = model.ParameterDefinitions.Select(d => d.Name == "compressionWindow" ? new StrategyParameterDefinition(d.Name, d.Minimum, d.Maximum, 4m, d.Description, d.Required, d.ValueType) : d).ToArray();
        Assert.Throws<ArgumentException>(() => model.Evaluate(Input(model, Signal(Enumerable.Repeat(100m, 13), 2m), new StrategyParameterSet(altered, Values()))));
    }

    [Fact]
    public void IsDeterministicAndHasNoTradeIntentOrExecutionDependency()
    {
        var model = new VolatilityCompressionBreakoutResearchModel();
        var input = Input(model, Signal([.. Enumerable.Repeat(100m, 12), 110m], 2m));
        var evaluation = new VolatilityCompressionBreakoutEvaluationInput(input, Gates(input));
        var first = model.Evaluate(evaluation);
        var second = model.Evaluate(evaluation);

        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.ObservedAtUtc, second.ObservedAtUtc);
        Assert.Equal(first.Rationale, second.Rationale);
        Assert.DoesNotContain(typeof(VolatilityCompressionBreakoutResearchModel).Assembly.GetReferencedAssemblies(), a => a.Name?.Contains("Execution", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(typeof(VolatilityCompressionBreakoutResearchModel).GetMembers(BindingFlags.Public | BindingFlags.Instance), member => member switch
        {
            PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
            MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType) || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
            _ => false
        });
    }

    private static StrategyEvaluationInput Input(VolatilityCompressionBreakoutResearchModel model, StrategyTimeframeSeries signal, StrategyParameterSet? parameters = null) =>
        new(model.TemplateId, parameters ?? Parameters(model), State(model), new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes), [Regime(), signal, Execution()]);
    private static StrategyState State(VolatilityCompressionBreakoutResearchModel model) => new(model.TemplateId, 0, s_asOfUtc.AddHours(-1));
    private static StrategyParameterSet Parameters(VolatilityCompressionBreakoutResearchModel model, decimal maxWidth = 3m, decimal volumeMultiplier = 1.5m) => new(model.ParameterDefinitions, Values(maxWidth, volumeMultiplier));
    private static Dictionary<string, StrategyParameterValue> Values(decimal maxWidth = 3m, decimal volumeMultiplier = 1.5m) => new()
    {
        ["bollingerPeriod"] = StrategyParameterValue.WholeNumber(10m), ["standardDeviationMultiplier"] = StrategyParameterValue.FromNumeric(2m),
        ["compressionWindow"] = StrategyParameterValue.WholeNumber(3m), ["maximumBandWidthPercent"] = StrategyParameterValue.FromNumeric(maxWidth),
        ["volumeLookback"] = StrategyParameterValue.WholeNumber(2m), ["volumeMultiplier"] = StrategyParameterValue.FromNumeric(volumeMultiplier)
    };
    private static StrategyTimeframeSeries Regime() => Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, [100m], 1m);
    private static StrategyTimeframeSeries Execution() => Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, [100m], 1m);
    private static StrategyTimeframeSeries Signal(IEnumerable<decimal> closes, decimal lastVolume, DateTimeOffset? end = null) => Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, closes, lastVolume, end);
    private static StrategyTimeframeSeries Series(StrategyTimeframeRole role, CandleInterval interval, IEnumerable<decimal> closes, decimal lastVolume, DateTimeOffset? end = null)
    {
        var values = closes.ToArray(); var closeAt = end ?? s_asOfUtc; var span = interval switch { CandleInterval.OneHour => TimeSpan.FromHours(1), CandleInterval.FifteenMinutes => TimeSpan.FromMinutes(15), _ => TimeSpan.FromMinutes(5) };
        return new StrategyTimeframeSeries(role, interval, values.Select((price, index) => Candle(interval, closeAt - span * (values.Length - index), closeAt - span * (values.Length - index - 1), price, index == values.Length - 1 ? lastVolume : 1m)).ToArray());
    }
    private static Candle Candle(CandleInterval interval, DateTimeOffset open, DateTimeOffset close, decimal price, decimal volume, bool safe = true) =>
        new("BTCUSD", interval, open, close, price - 1m, price + 1m, price - 1m, price, volume, true, false, safe ? [] : [DataQualityIssue.Stale]);
    private static RejectionGateEvaluation Gates(StrategyEvaluationInput input, bool quality = true)
    {
        var instrument = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var approval = StrategyApproval.CreateDraft(Guid.NewGuid(), new StrategyVersion(new StrategyTemplateVersionIdentity("platform.volatility-compression-breakout", 1), new StrategyParameterSchemaReference("volatility-compression-breakout-parameters", 1, Fingerprint), Fingerprint, s_asOfUtc), StrategyApprovalActor.Human(Guid.NewGuid()), s_asOfUtc, new StrategyApprovalRequirements([new ApprovedInstrumentScope(AssetClass.Cryptocurrency, instrument)], 100, 100m, .10m, .01m, TimeSpan.FromMinutes(30), [CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes], [TradingProductType.Spot], [StrategyApprovalMode.Backtest], input.Timeframes));
        var evidence = new StrategyResearchEvidence(new ResearchEvidenceProvenance("recorded-volatility-compression-research", Fingerprint, s_asOfUtc), new StrategyApprovalEvidence(instrument, AssetClass.Cryptocurrency, 100, 100m, .10m, .01m, s_asOfUtc), quality, .03m, .02m, true, .01m, .90m, .90m, Fingerprint);
        return StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(new StrategyRejectionGateEvaluationInput(approval, input.Timeframes, TradingProductType.Spot, StrategyApprovalMode.Backtest, evidence, s_asOfUtc));
    }
}
