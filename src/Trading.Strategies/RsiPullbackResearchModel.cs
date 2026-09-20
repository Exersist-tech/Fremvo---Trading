using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Strategies;
using Trading.Indicators;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored, versioned, closed-candle RSI pullback research model.
/// It observes a bounded signal-timeframe pullback only within a defined
/// higher-timeframe EMA uptrend; it is not a regime classifier.
/// </summary>
public sealed class RsiPullbackResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string RegimeFastPeriodParameter = "regimeFastPeriod";
    private const string RegimeSlowPeriodParameter = "regimeSlowPeriod";
    private const string RsiPeriodParameter = "rsiPeriod";
    private const string MaximumPullbackRsiParameter = "maximumPullbackRsi";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(RegimeFastPeriodParameter, 10m, 30m, 20m, "Completed higher-timeframe EMA fast period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(RegimeSlowPeriodParameter, 30m, 100m, 50m, "Completed higher-timeframe EMA slow period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(RsiPeriodParameter, 5m, 30m, 14m, "Completed signal-timeframe decimal RSI period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(MaximumPullbackRsiParameter, 20m, 50m, 35m, "Maximum decimal RSI value, inclusive, for a completed signal-timeframe pullback.", valueType: StrategyParameterValueType.Numeric)
        ]);

    public RsiPullbackResearchModel()
        : base(
            "rsi-pullback-v1",
            "RSI Pullback Research",
            TradingProductType.Spot,
            "RSI pullback research within a defined higher-timeframe EMA trend using only completed, safe candles.",
            "regime EMA: 10-30 / 30-100; RSI: 5-30; maximum pullback RSI: 20-50",
            "A completed higher-timeframe EMA uptrend and a completed signal-timeframe decimal RSI at or below its bounded pullback threshold.",
            "Requires accepted rejection gates, configured Regime/Signal/Execution roles, sufficient completed warmup, and safe as-of-aligned candles.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("rsi-pullback-v1");

    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        return Neutral(input, "Unavailable: accepted rejection-gate evidence is required before RSI pullback research evaluation.");
    }

    public StrategyAnalysisProposal Evaluate(RsiPullbackEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var input = evaluation.EvaluationInput;
        ValidateInput(input);

        if (!evaluation.HasAcceptedGatesAt(input.AsOfUtc))
        {
            return Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle.");
        }

        var regimeFastPeriod = Period(input, RegimeFastPeriodParameter);
        var regimeSlowPeriod = Period(input, RegimeSlowPeriodParameter);
        var rsiPeriod = Period(input, RsiPeriodParameter);
        var requiredRegime = regimeSlowPeriod + 1;
        var requiredSignal = rsiPeriod + 1;
        if (input.Regime.ClosedCandles.Count < requiredRegime
            || input.Signal.ClosedCandles.Count < requiredSignal
            || input.Execution.ClosedCandles.Count < 1)
        {
            return Neutral(
                input,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unavailable: requires {requiredRegime} regime, {requiredSignal} signal, and 1 execution completed candle."));
        }

        var regime = input.Regime.ClosedCandles;
        var fastNow = Ema(regime, regimeFastPeriod);
        var slowNow = Ema(regime, regimeSlowPeriod);
        var fastPrevious = Ema(regime.Take(regime.Count - 1).ToArray(), regimeFastPeriod);
        if (fastNow <= slowNow || fastNow <= fastPrevious || regime[^1].Close <= slowNow)
        {
            return Neutral(input, "No condition: the completed higher-timeframe EMA role is not a rising uptrend.");
        }

        var rsi = Rsi(input.Signal.ClosedCandles, rsiPeriod);
        var maximumRsi = input.Parameters.GetDecimal(MaximumPullbackRsiParameter);
        if (rsi > maximumRsi)
        {
            return Neutral(
                input,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No condition: completed signal RSI {rsi:F4} exceeds the pullback threshold {maximumRsi:F4}."));
        }

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Bullish,
            0.50m,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Completed RSI pullback observation: higher-timeframe EMA fast/slow {fastNow:F4}/{slowNow:F4}, signal RSI {rsi:F4} <= {maximumRsi:F4}; analysis only, not a trade instruction."));
    }

    private static decimal Ema(IReadOnlyList<Candle> candles, int period) =>
        new ExponentialMovingAverageCalculator(period).Calculate(candles).Value
        ?? throw new InvalidOperationException("EMA warmup was checked before calculation.");

    private static decimal Rsi(IReadOnlyList<Candle> candles, int period) =>
        new RelativeStrengthIndexCalculator(period).Calculate(candles).Value
        ?? throw new InvalidOperationException("RSI warmup was checked before calculation.");

    private static StrategyAnalysisProposal Neutral(StrategyEvaluationInput input, string rationale) =>
        new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Neutral, 0m, rationale);

    private static int Period(StrategyEvaluationInput input, string parameterName) =>
        decimal.ToInt32(input.Parameters.GetDecimal(parameterName));

    private void ValidateInput(StrategyEvaluationInput input)
    {
        if (!TemplateId.Equals(input.TemplateId))
        {
            throw new ArgumentException("Evaluation input belongs to a different template.", nameof(input));
        }

        ValidateParameters(input.Parameters);
        if (input.Timeframes.Regime == input.Timeframes.Signal)
        {
            throw new ArgumentException("RSI pullback requires a distinct higher-timeframe regime role.", nameof(input));
        }
    }

    private static void ValidateParameters(StrategyParameterSet parameters)
    {
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("RSI pullback accepts only its platform-defined parameters.", nameof(parameters));
        }

        foreach (var expected in s_parameterDefinitions)
        {
            StrategyParameterDefinition actual;
            try
            {
                actual = parameters.GetDefinition(expected.Name);
            }
            catch (KeyNotFoundException exception)
            {
                throw new ArgumentException($"RSI pullback requires parameter '{expected.Name}'.", nameof(parameters), exception);
            }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum
                || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required
                || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(parameters));
            }
        }

        if (parameters.GetDecimal(RegimeFastPeriodParameter) >= parameters.GetDecimal(RegimeSlowPeriodParameter))
        {
            throw new ArgumentException("The higher-timeframe fast EMA period must be strictly less than the slow EMA period.", nameof(parameters));
        }
    }
}

/// <summary>
/// Couples immutable market data with recorded governance gates and has no
/// order, account, sizing, leverage, averaging, or execution capability.
/// </summary>
public sealed class RsiPullbackEvaluationInput
{
    public RsiPullbackEvaluationInput(
        StrategyEvaluationInput evaluationInput,
        RejectionGateEvaluation? rejectionGates)
    {
        ArgumentNullException.ThrowIfNull(evaluationInput);
        EvaluationInput = evaluationInput;
        RejectionGates = rejectionGates;
    }

    public StrategyEvaluationInput EvaluationInput { get; }
    public RejectionGateEvaluation? RejectionGates { get; }

    internal bool HasAcceptedGatesAt(DateTimeOffset asOfUtc) =>
        RejectionGates is { Accepted: true, Results.Count: > 0 }
        && RejectionGates.Results.All(result =>
            result.Status == RejectionGateStatus.Passed
            && result.EvaluatedAtUtc == asOfUtc);
}
