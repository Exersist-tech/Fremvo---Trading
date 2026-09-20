using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Strategies;
using Trading.Indicators;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored, versioned, closed-candle volatility-compression breakout research model.
/// </summary>
public sealed class VolatilityCompressionBreakoutResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string BollingerPeriodParameter = "bollingerPeriod";
    private const string StandardDeviationMultiplierParameter = "standardDeviationMultiplier";
    private const string CompressionWindowParameter = "compressionWindow";
    private const string MaximumBandWidthPercentParameter = "maximumBandWidthPercent";
    private const string VolumeLookbackParameter = "volumeLookback";
    private const string VolumeMultiplierParameter = "volumeMultiplier";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(BollingerPeriodParameter, 10m, 40m, 20m, "Completed signal candles used for each Bollinger-band calculation.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(StandardDeviationMultiplierParameter, 1.5m, 3m, 2m, "Decimal standard-deviation multiplier for Bollinger bands.", valueType: StrategyParameterValueType.Numeric),
            new StrategyParameterDefinition(CompressionWindowParameter, 2m, 20m, 5m, "Consecutive completed candles immediately before the breakout that must be compressed.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(MaximumBandWidthPercentParameter, 0m, 10m, 3m, "Inclusive maximum decimal Bollinger width percentage for every prior compression candle.", valueType: StrategyParameterValueType.Numeric),
            new StrategyParameterDefinition(VolumeLookbackParameter, 2m, 60m, 20m, "Preceding completed signal candles used for the decimal breakout-volume baseline.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(VolumeMultiplierParameter, 1m, 10m, 1.5m, "Inclusive decimal multiplier required above the preceding completed volume baseline.", valueType: StrategyParameterValueType.Numeric)
        ]);

    public VolatilityCompressionBreakoutResearchModel()
        : base(
            "volatility-compression-breakout-v1",
            "Volatility Compression Breakout Research",
            TradingProductType.Spot,
            "Bollinger-width compression and breakout research using only completed, safe, as-of-aligned candles.",
            "bands: 10-40, 1.5-3.0 deviations; compression: 2-20, max width: 0-10%; volume: 2-60, multiplier: 1-10",
            "Every candle in the immediately preceding compression window must have inclusive bounded Bollinger width; only the subsequent current completed candle may break strictly above or below its current Bollinger boundary with inclusive volume confirmation.",
            "Requires accepted rejection gates, configured Regime/Signal/Execution roles, sufficient completed warmup, and safe as-of-aligned candles.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("volatility-compression-breakout-v1");
    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        return Neutral(input, "Unavailable: accepted rejection-gate evidence is required before volatility-compression breakout research evaluation.");
    }

    public StrategyAnalysisProposal Evaluate(VolatilityCompressionBreakoutEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var input = evaluation.EvaluationInput;
        ValidateInput(input);

        if (!evaluation.HasAcceptedGatesAt(input.AsOfUtc))
        {
            return Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle.");
        }

        var bollingerPeriod = Period(input, BollingerPeriodParameter);
        var compressionWindow = Period(input, CompressionWindowParameter);
        var volumeLookback = Period(input, VolumeLookbackParameter);
        var requiredSignal = Math.Max(bollingerPeriod + compressionWindow, volumeLookback + 1);
        if (input.Signal.ClosedCandles.Count < requiredSignal || input.Execution.ClosedCandles.Count < 1)
        {
            return Neutral(input, string.Create(CultureInfo.InvariantCulture, $"Unavailable: requires {requiredSignal} signal and 1 execution completed candle."));
        }

        var candles = input.Signal.ClosedCandles;
        var multiplier = input.Parameters.GetDecimal(StandardDeviationMultiplierParameter);
        var maximumWidth = input.Parameters.GetDecimal(MaximumBandWidthPercentParameter);
        for (var offset = compressionWindow; offset >= 1; offset--)
        {
            var compressionBands = Bands(candles.Take(candles.Count - offset).ToArray(), bollingerPeriod, multiplier);
            if (compressionBands.Middle <= 0m)
            {
                return Neutral(input, "Unavailable: compression cannot calculate a band-width percentage from a non-positive completed Bollinger middle band.");
            }

            var widthPercent = ((compressionBands.Upper - compressionBands.Lower) / compressionBands.Middle) * 100m;
            if (widthPercent > maximumWidth)
            {
                return Neutral(input, string.Create(CultureInfo.InvariantCulture, $"No condition: the prior completed compression window exceeded the {maximumWidth:F4}% band-width limit."));
            }
        }

        var currentBands = Bands(candles, bollingerPeriod, multiplier);
        var direction = candles[^1].Close > currentBands.Upper
            ? StrategyAnalysisDirection.Bullish
            : candles[^1].Close < currentBands.Lower
                ? StrategyAnalysisDirection.Bearish
                : StrategyAnalysisDirection.Neutral;
        if (direction == StrategyAnalysisDirection.Neutral)
        {
            return Neutral(input, "No condition: the current completed candle did not strictly break a current Bollinger boundary after the prior compression window.");
        }

        var baseline = candles.Skip(candles.Count - volumeLookback - 1).Take(volumeLookback).Average(candle => candle.Volume);
        var requiredVolume = baseline * input.Parameters.GetDecimal(VolumeMultiplierParameter);
        if (candles[^1].Volume < requiredVolume)
        {
            return Neutral(input, string.Create(CultureInfo.InvariantCulture, $"No condition: completed breakout volume {candles[^1].Volume:F4} is below required confirmation {requiredVolume:F4}."));
        }

        var boundary = direction == StrategyAnalysisDirection.Bullish ? currentBands.Upper : currentBands.Lower;
        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            direction,
            0.50m,
            string.Create(CultureInfo.InvariantCulture, $"Completed volatility-compression breakout observation: {compressionWindow} prior completed candles met the inclusive {maximumWidth:F4}% width limit; current close {candles[^1].Close:F4} strictly broke boundary {boundary:F4}, volume {candles[^1].Volume:F4} >= {requiredVolume:F4}; analysis only, not a trade instruction."));
    }

    private static BollingerBandsValue Bands(IReadOnlyList<Candle> candles, int period, decimal multiplier) =>
        new BollingerBandsCalculator(period, multiplier).Calculate(candles).Value
        ?? throw new InvalidOperationException("Bollinger warmup was checked before calculation.");

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
            throw new ArgumentException("Volatility compression breakout accepts only its platform-defined parameters.", nameof(input));
        }

        foreach (var expected in s_parameterDefinitions)
        {
            StrategyParameterDefinition actual;
            try { actual = input.Parameters.GetDefinition(expected.Name); }
            catch (KeyNotFoundException exception) { throw new ArgumentException($"Volatility compression breakout requires parameter '{expected.Name}'.", nameof(input), exception); }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(input));
            }
        }
    }
}

/// <summary>
/// Couples immutable market data with recorded governance gates and has no execution capability.
/// </summary>
public sealed class VolatilityCompressionBreakoutEvaluationInput
{
    public VolatilityCompressionBreakoutEvaluationInput(StrategyEvaluationInput evaluationInput, RejectionGateEvaluation? rejectionGates)
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
