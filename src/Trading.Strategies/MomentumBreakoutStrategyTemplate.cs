using Trading.Domain.Strategies;

namespace Trading.Strategies;

/// <summary>
/// A compiled, platform-owned closed-candle breakout analysis template.
/// It produces observations only; it neither sizes nor submits trades.
/// </summary>
public sealed class MomentumBreakoutStrategyTemplate : ApprovedStrategyTemplate, IStrategy
{
    private const string LookbackPeriodsParameter = "lookbackPeriods";
    private const string BreakoutThresholdPercentParameter = "breakoutThresholdPercent";
    private const string VolumeMultiplierParameter = "volumeMultiplier";

    private static readonly System.Collections.ObjectModel.ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(
                LookbackPeriodsParameter,
                5m,
                50m,
                20m,
                "Number of completed preceding candles used for the breakout range.",
                valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(
                BreakoutThresholdPercentParameter,
                0.5m,
                3m,
                1m,
                "Minimum percentage by which the closed price must meet or exceed the preceding range high.",
                valueType: StrategyParameterValueType.Numeric),
            new StrategyParameterDefinition(
                VolumeMultiplierParameter,
                1m,
                3m,
                1.2m,
                "Minimum multiple of preceding average closed-candle volume required for confirmation.",
                valueType: StrategyParameterValueType.Numeric)
        ]);

    public MomentumBreakoutStrategyTemplate()
        : base(
            "momentum-breakout-v1",
            "Momentum Breakout",
            TradingProductType.Spot,
            "Trend continuation strategy that reacts to a confirmed breakout from recent range highs.",
            "periods: 5-50, breakoutThresholdPct: 0.5-3.0, volumeMultiplier: 1.0-3.0",
            "EMA(20), breakout above prior swing high, volume expansion",
            "Requires closed candles, valid range high, and no stale market data.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("momentum-breakout-v1");

    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TemplateId.Equals(input.TemplateId))
        {
            throw new ArgumentException("Evaluation input belongs to a different template.", nameof(input));
        }

        ValidateParameters(input.Parameters);

        var lookback = decimal.ToInt32(input.Parameters.GetDecimal(LookbackPeriodsParameter));
        if (input.ClosedCandles.Count < lookback + 2)
        {
            return Neutral(
                input,
                $"Unavailable: at least {lookback + 2} closed candles are required for breakout edge evaluation.");
        }

        var threshold = input.Parameters.GetDecimal(BreakoutThresholdPercentParameter) / 100m;
        var volumeMultiplier = input.Parameters.GetDecimal(VolumeMultiplierParameter);
        var candidateIndex = input.ClosedCandles.Count - 1;

        var currentIsBreakout = IsConfirmedBreakout(
            input.ClosedCandles,
            candidateIndex,
            lookback,
            threshold,
            volumeMultiplier);
        if (!currentIsBreakout)
        {
            return Neutral(input, "No bullish breakout confirmation on the latest closed candle.");
        }

        var previousWasBreakout = IsConfirmedBreakout(
            input.ClosedCandles,
            candidateIndex - 1,
            lookback,
            threshold,
            volumeMultiplier);
        if (previousWasBreakout)
        {
            return Neutral(input, "No new breakout edge: the preceding closed candle was already confirmed.");
        }

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Bullish,
            0.55m,
            "New closed-candle range breakout with volume confirmation; analysis only, not a trade instruction.");
    }

    private static bool IsConfirmedBreakout(
        IReadOnlyList<Trading.MarketData.Candle> candles,
        int candidateIndex,
        int lookback,
        decimal threshold,
        decimal volumeMultiplier)
    {
        var rangeStart = candidateIndex - lookback;
        if (rangeStart < 0)
        {
            return false;
        }

        var rangeHigh = 0m;
        var volumeTotal = 0m;
        for (var index = rangeStart; index < candidateIndex; index++)
        {
            rangeHigh = Math.Max(rangeHigh, candles[index].High);
            volumeTotal += candles[index].Volume;
        }

        if (rangeHigh <= 0m || volumeTotal <= 0m)
        {
            return false;
        }

        var minimumClose = rangeHigh * (1m + threshold);
        var minimumVolume = (volumeTotal / lookback) * volumeMultiplier;
        var candidate = candles[candidateIndex];
        return candidate.Close >= minimumClose && candidate.Volume >= minimumVolume;
    }

    private static StrategyAnalysisProposal Neutral(StrategyEvaluationInput input, string rationale) =>
        new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Neutral, 0m, rationale);

    private static void ValidateParameters(StrategyParameterSet parameters)
    {
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("Momentum breakout accepts only its platform-defined parameters.", nameof(parameters));
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
                throw new ArgumentException(
                    $"Momentum breakout requires parameter '{expected.Name}'.",
                    nameof(parameters),
                    exception);
            }

            if (actual.Minimum != expected.Minimum
                || actual.Maximum != expected.Maximum
                || actual.DefaultValue != expected.DefaultValue
                || actual.Required != expected.Required
                || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException(
                    $"Parameter '{expected.Name}' does not match the platform-defined bounds and type.",
                    nameof(parameters));
            }
        }
    }
}
