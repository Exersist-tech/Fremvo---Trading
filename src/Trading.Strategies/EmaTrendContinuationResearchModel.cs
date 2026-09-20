using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Strategies;
using Trading.Indicators;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored, versioned, closed-candle research model. Its output is
/// an analysis observation only and is not an order or trading instruction.
/// </summary>
public sealed class EmaTrendContinuationResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string RegimeFastPeriodParameter = "regimeFastPeriod";
    private const string RegimeSlowPeriodParameter = "regimeSlowPeriod";
    private const string SignalFastPeriodParameter = "signalFastPeriod";
    private const string SignalSlowPeriodParameter = "signalSlowPeriod";
    private const string ConfirmationCandlesParameter = "confirmationCandles";
    private const string RetracementPercentParameter = "retracementPercent";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(RegimeFastPeriodParameter, 10m, 30m, 20m, "Completed higher-timeframe EMA fast period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(RegimeSlowPeriodParameter, 30m, 100m, 50m, "Completed higher-timeframe EMA slow period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(SignalFastPeriodParameter, 5m, 20m, 10m, "Completed signal-timeframe EMA fast period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(SignalSlowPeriodParameter, 15m, 50m, 25m, "Completed signal-timeframe EMA slow period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(ConfirmationCandlesParameter, 1m, 5m, 2m, "Completed signal candles required to confirm recovery.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(RetracementPercentParameter, 0.25m, 3m, 1m, "Maximum percentage above the signal fast EMA at which a completed pullback may begin.", valueType: StrategyParameterValueType.Numeric)
        ]);

    public EmaTrendContinuationResearchModel()
        : base(
            "ema-trend-continuation-v1",
            "EMA Trend Continuation Research",
            TradingProductType.Spot,
            "Multi-timeframe EMA trend-continuation research using only completed, safe candles.",
            "regime EMA: 10-30 / 30-100; signal EMA: 5-20 / 15-50; confirmation: 1-5; retracementPct: 0.25-3.0",
            "Higher-timeframe rising EMA regime, signal-timeframe completed pullback recovery, and execution-timeframe close confirmation.",
            "Requires accepted rejection gates, configured Regime/Signal/Execution roles, sufficient completed warmup, and safe as-of-aligned candles.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("ema-trend-continuation-v1");

    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    /// <summary>
    /// The generic strategy surface has no governance evidence, so it fails
    /// closed. Call the contextual overload with recorded gate results.
    /// </summary>
    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        return Neutral(input, "Unavailable: accepted rejection-gate evidence is required before EMA research evaluation.");
    }

    public StrategyAnalysisProposal Evaluate(EmaTrendContinuationEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var input = evaluation.EvaluationInput;
        ValidateInput(input);

        if (!evaluation.HasAcceptedGatesAt(input.AsOfUtc))
        {
            return Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle.");
        }

        var regimeFast = Period(input, RegimeFastPeriodParameter);
        var regimeSlow = Period(input, RegimeSlowPeriodParameter);
        var signalFast = Period(input, SignalFastPeriodParameter);
        var signalSlow = Period(input, SignalSlowPeriodParameter);
        var confirmations = Period(input, ConfirmationCandlesParameter);
        var retracement = input.Parameters.GetDecimal(RetracementPercentParameter) / 100m;

        var requiredRegime = regimeSlow + 1;
        var requiredSignal = signalSlow + confirmations;
        if (input.Regime.ClosedCandles.Count < requiredRegime
            || input.Signal.ClosedCandles.Count < requiredSignal
            || input.Execution.ClosedCandles.Count < 2)
        {
            return Neutral(
                input,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unavailable: requires {requiredRegime} regime, {requiredSignal} signal, and 2 execution completed candles."));
        }

        var regimeFastNow = Ema(input.Regime.ClosedCandles, regimeFast);
        var regimeSlowNow = Ema(input.Regime.ClosedCandles, regimeSlow);
        var regimeFastPrevious = Ema(input.Regime.ClosedCandles.Take(input.Regime.ClosedCandles.Count - 1).ToArray(), regimeFast);
        if (regimeFastNow <= regimeSlowNow
            || regimeFastNow <= regimeFastPrevious
            || input.Regime.ClosedCandles[^1].Close <= regimeFastNow)
        {
            return Neutral(input, "No condition: the higher-timeframe completed EMA regime is not a rising uptrend.");
        }

        var signalFastNow = Ema(input.Signal.ClosedCandles, signalFast);
        var signalSlowNow = Ema(input.Signal.ClosedCandles, signalSlow);
        if (signalFastNow <= signalSlowNow)
        {
            return Neutral(input, "No condition: the signal-timeframe completed EMA structure is not bullish.");
        }

        if (!HasCompletedRetracementAndRecovery(input.Signal.ClosedCandles, signalFast, confirmations, retracement))
        {
            return Neutral(input, "No condition: no bounded completed pullback and recovery confirms continuation.");
        }

        var execution = input.Execution.ClosedCandles;
        if (execution[^1].Close <= execution[^2].Close || execution[^1].Close <= execution[^1].Open)
        {
            return Neutral(input, "No condition: the completed execution candle does not confirm upward continuation.");
        }

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Bullish,
            0.60m,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Completed EMA continuation observation: regime fast/slow {regimeFastNow:F4}/{regimeSlowNow:F4}, signal fast/slow {signalFastNow:F4}/{signalSlowNow:F4}; analysis only, not a trade instruction."));
    }

    private static bool HasCompletedRetracementAndRecovery(
        IReadOnlyList<Candle> candles,
        int fastPeriod,
        int confirmations,
        decimal retracement)
    {
        var recoveryStart = candles.Count - confirmations;
        var pullbackIndex = recoveryStart - 1;
        if (pullbackIndex < fastPeriod - 1)
        {
            return false;
        }

        var pullbackFast = Ema(candles.Take(pullbackIndex + 1).ToArray(), fastPeriod);
        if (candles[pullbackIndex].Low > pullbackFast * (1m + retracement))
        {
            return false;
        }

        for (var index = recoveryStart; index < candles.Count; index++)
        {
            var fast = Ema(candles.Take(index + 1).ToArray(), fastPeriod);
            if (candles[index].Close <= fast || (index > recoveryStart && candles[index].Close <= candles[index - 1].Close))
            {
                return false;
            }
        }

        return true;
    }

    private static decimal Ema(IReadOnlyList<Candle> candles, int period) =>
        new ExponentialMovingAverageCalculator(period).Calculate(candles).Value
        ?? throw new InvalidOperationException("EMA warmup was checked before calculation.");

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
            throw new ArgumentException("EMA trend continuation requires a distinct higher-timeframe regime role.", nameof(input));
        }
    }

    private static void ValidateParameters(StrategyParameterSet parameters)
    {
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("EMA trend continuation accepts only its platform-defined parameters.", nameof(parameters));
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
                throw new ArgumentException($"EMA trend continuation requires parameter '{expected.Name}'.", nameof(parameters), exception);
            }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum
                || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required
                || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(parameters));
            }
        }

        if (parameters.GetDecimal(RegimeFastPeriodParameter) >= parameters.GetDecimal(RegimeSlowPeriodParameter)
            || parameters.GetDecimal(SignalFastPeriodParameter) >= parameters.GetDecimal(SignalSlowPeriodParameter))
        {
            throw new ArgumentException("EMA fast periods must be strictly less than their corresponding slow periods.", nameof(parameters));
        }
    }
}

/// <summary>
/// Couples the immutable market snapshot with recorded governance gates. It
/// intentionally cannot carry orders, account data, sizing, or execution data.
/// </summary>
public sealed class EmaTrendContinuationEvaluationInput
{
    public EmaTrendContinuationEvaluationInput(
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
