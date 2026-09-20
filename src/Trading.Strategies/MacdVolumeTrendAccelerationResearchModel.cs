using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Strategies;
using Trading.Indicators;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored, versioned, closed-candle MACD and volume trend-acceleration research model.
/// </summary>
public sealed class MacdVolumeTrendAccelerationResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string FastPeriodParameter = "fastPeriod";
    private const string SlowPeriodParameter = "slowPeriod";
    private const string SignalPeriodParameter = "signalPeriod";
    private const string VolumeLookbackParameter = "volumeLookback";
    private const string VolumeMultiplierParameter = "volumeMultiplier";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(FastPeriodParameter, 3m, 30m, 12m, "Completed signal-timeframe MACD fast EMA period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(SlowPeriodParameter, 10m, 100m, 26m, "Completed signal-timeframe MACD slow EMA period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(SignalPeriodParameter, 2m, 30m, 9m, "Completed signal-timeframe MACD signal EMA period.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(VolumeLookbackParameter, 2m, 60m, 20m, "Number of preceding completed signal candles used for the decimal volume baseline.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(VolumeMultiplierParameter, 1m, 10m, 1.5m, "Inclusive decimal multiplier required above the preceding completed volume baseline.", valueType: StrategyParameterValueType.Numeric)
        ]);

    public MacdVolumeTrendAccelerationResearchModel()
        : base(
            "macd-volume-trend-acceleration-v1",
            "MACD Volume Trend Acceleration Research",
            TradingProductType.Spot,
            "MACD crossover and volume-confirmed trend acceleration research using only completed, safe candles.",
            "MACD: fast 3-30, slow 10-100, signal 2-30; volume lookback: 2-60; multiplier: 1-10",
            "A completed bullish MACD line crossover with a positive, increasing histogram and current volume at or above its preceding completed-candle baseline multiplier.",
            "Requires accepted rejection gates, configured Regime/Signal/Execution roles, sufficient completed warmup, and safe as-of-aligned candles.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("macd-volume-trend-acceleration-v1");
    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        return Neutral(input, "Unavailable: accepted rejection-gate evidence is required before MACD volume trend-acceleration research evaluation.");
    }

    public StrategyAnalysisProposal Evaluate(MacdVolumeTrendAccelerationEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var input = evaluation.EvaluationInput;
        ValidateInput(input);

        if (!evaluation.HasAcceptedGatesAt(input.AsOfUtc))
        {
            return Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle.");
        }

        var fast = Period(input, FastPeriodParameter);
        var slow = Period(input, SlowPeriodParameter);
        var signal = Period(input, SignalPeriodParameter);
        var volumeLookback = Period(input, VolumeLookbackParameter);
        var requiredMacd = slow + signal;
        var requiredSignal = Math.Max(requiredMacd, volumeLookback + 1);
        if (input.Signal.ClosedCandles.Count < requiredSignal || input.Execution.ClosedCandles.Count < 1)
        {
            return Neutral(input, string.Create(CultureInfo.InvariantCulture, $"Unavailable: requires {requiredSignal} signal and 1 execution completed candle."));
        }

        var candles = input.Signal.ClosedCandles;
        var current = Macd(candles, fast, slow, signal);
        var previous = Macd(candles.Take(candles.Count - 1).ToArray(), fast, slow, signal);
        if (previous.Line > previous.Signal || current.Line <= current.Signal || current.Histogram <= 0m || current.Histogram <= previous.Histogram)
        {
            return Neutral(input, "No condition: completed MACD does not show a bullish crossover with increasing positive histogram acceleration.");
        }

        var baseline = candles.Skip(candles.Count - volumeLookback - 1).Take(volumeLookback).Average(candle => candle.Volume);
        var multiplier = input.Parameters.GetDecimal(VolumeMultiplierParameter);
        var requiredVolume = baseline * multiplier;
        if (candles[^1].Volume < requiredVolume)
        {
            return Neutral(input, string.Create(CultureInfo.InvariantCulture, $"No condition: completed volume {candles[^1].Volume:F4} is below required confirmation {requiredVolume:F4}."));
        }

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Bullish,
            0.50m,
            string.Create(CultureInfo.InvariantCulture, $"Completed MACD volume trend-acceleration observation: line/signal/histogram {current.Line:F4}/{current.Signal:F4}/{current.Histogram:F4}, volume {candles[^1].Volume:F4} >= {requiredVolume:F4}; analysis only, not a trade instruction."));
    }

    private static MacdValue Macd(IReadOnlyList<Candle> candles, int fast, int slow, int signal) =>
        new MovingAverageConvergenceDivergenceCalculator(fast, slow, signal).Calculate(candles).Value
        ?? throw new InvalidOperationException("MACD warmup was checked before calculation.");

    private static StrategyAnalysisProposal Neutral(StrategyEvaluationInput input, string rationale) =>
        new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Neutral, 0m, rationale);

    private static int Period(StrategyEvaluationInput input, string name) => decimal.ToInt32(input.Parameters.GetDecimal(name));

    private void ValidateInput(StrategyEvaluationInput input)
    {
        if (!TemplateId.Equals(input.TemplateId))
        {
            throw new ArgumentException("Evaluation input belongs to a different template.", nameof(input));
        }

        if (input.Parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("MACD volume trend acceleration accepts only its platform-defined parameters.", nameof(input));
        }

        foreach (var expected in s_parameterDefinitions)
        {
            StrategyParameterDefinition actual;
            try { actual = input.Parameters.GetDefinition(expected.Name); }
            catch (KeyNotFoundException exception) { throw new ArgumentException($"MACD volume trend acceleration requires parameter '{expected.Name}'.", nameof(input), exception); }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(input));
            }
        }

        if (input.Parameters.GetDecimal(FastPeriodParameter) >= input.Parameters.GetDecimal(SlowPeriodParameter))
        {
            throw new ArgumentException("The MACD fast period must be strictly less than the slow period.", nameof(input));
        }
    }
}

/// <summary>
/// Couples immutable market data with recorded governance gates and has no execution capability.
/// </summary>
public sealed class MacdVolumeTrendAccelerationEvaluationInput
{
    public MacdVolumeTrendAccelerationEvaluationInput(StrategyEvaluationInput evaluationInput, RejectionGateEvaluation? rejectionGates)
    {
        EvaluationInput = evaluationInput ?? throw new ArgumentNullException(nameof(evaluationInput));
        RejectionGates = rejectionGates;
    }

    public StrategyEvaluationInput EvaluationInput { get; }
    public RejectionGateEvaluation? RejectionGates { get; }

    internal bool HasAcceptedGatesAt(DateTimeOffset asOfUtc) =>
        RejectionGates is { Accepted: true, Results.Count: > 0 }
        && RejectionGates.Results.All(result => result.Status == RejectionGateStatus.Passed && result.EvaluatedAtUtc == asOfUtc);
}
