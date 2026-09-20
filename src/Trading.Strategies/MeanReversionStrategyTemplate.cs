using Trading.Domain.Strategies;

namespace Trading.Strategies;

/// <summary>
/// A compiled, platform-owned closed-candle mean-reversion analysis template.
/// It identifies a condition only; it neither sizes, accumulates, nor submits trades.
/// </summary>
public sealed class MeanReversionStrategyTemplate : ApprovedStrategyTemplate, IStrategy
{
    private const string LookbackPeriodsParameter = "lookbackPeriods";
    private const string DeviationThresholdPercentParameter = "deviationThresholdPercent";
    private const string VolumeMultiplierParameter = "volumeMultiplier";

    private static readonly System.Collections.ObjectModel.ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(
                LookbackPeriodsParameter,
                5m,
                50m,
                20m,
                "Number of completed preceding candles used to calculate the simple moving average.",
                valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(
                DeviationThresholdPercentParameter,
                0.5m,
                10m,
                2m,
                "Minimum percentage by which the latest closed price must be below the preceding simple moving average.",
                valueType: StrategyParameterValueType.Numeric),
            new StrategyParameterDefinition(
                VolumeMultiplierParameter,
                1m,
                3m,
                1.2m,
                "Minimum multiple of preceding average closed-candle volume required for confirmation.",
                valueType: StrategyParameterValueType.Numeric)
        ]);

    public MeanReversionStrategyTemplate()
        : base(
            "mean-reversion-v1",
            "Mean Reversion",
            TradingProductType.Spot,
            "Relative-value analysis that identifies a confirmed closed price materially below its preceding simple moving average.",
            "periods: 5-50, deviationThresholdPct: 0.5-10.0, volumeMultiplier: 1.0-3.0",
            "preceding SMA, negative close deviation, volume confirmation",
            "Requires closed candles, a valid mean range, and no stale or incomplete data.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("mean-reversion-v1");

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
                $"Unavailable: at least {lookback + 2} closed candles are required for mean-reversion edge evaluation.");
        }

        var deviationThreshold = input.Parameters.GetDecimal(DeviationThresholdPercentParameter) / 100m;
        var volumeMultiplier = input.Parameters.GetDecimal(VolumeMultiplierParameter);
        var candidateIndex = input.ClosedCandles.Count - 1;

        if (!IsConfirmedMeanReversion(
                input.ClosedCandles,
                candidateIndex,
                lookback,
                deviationThreshold,
                volumeMultiplier))
        {
            return Neutral(input, "No confirmed mean-reversion condition on the latest closed candle.");
        }

        if (IsConfirmedMeanReversion(
                input.ClosedCandles,
                candidateIndex - 1,
                lookback,
                deviationThreshold,
                volumeMultiplier))
        {
            return Neutral(input, "No new mean-reversion edge: the preceding closed candle was already confirmed.");
        }

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Bullish,
            0.55m,
            "New closed-candle negative SMA deviation with volume confirmation; analysis only, not a trade instruction.");
    }

    private static bool IsConfirmedMeanReversion(
        IReadOnlyList<Trading.MarketData.Candle> candles,
        int candidateIndex,
        int lookback,
        decimal deviationThreshold,
        decimal volumeMultiplier)
    {
        var historyStart = candidateIndex - lookback;
        if (historyStart < 0)
        {
            return false;
        }

        var closeTotal = 0m;
        var volumeTotal = 0m;
        for (var index = historyStart; index < candidateIndex; index++)
        {
            closeTotal += candles[index].Close;
            volumeTotal += candles[index].Volume;
        }

        if (closeTotal <= 0m || volumeTotal <= 0m)
        {
            return false;
        }

        var averageClose = closeTotal / lookback;
        var minimumVolume = (volumeTotal / lookback) * volumeMultiplier;
        var maximumClose = averageClose * (1m - deviationThreshold);
        var candidate = candles[candidateIndex];
        return candidate.Close <= maximumClose && candidate.Volume >= minimumVolume;
    }

    private static StrategyAnalysisProposal Neutral(StrategyEvaluationInput input, string rationale) =>
        new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Neutral, 0m, rationale);

    private static void ValidateParameters(StrategyParameterSet parameters)
    {
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("Mean reversion accepts only its platform-defined parameters.", nameof(parameters));
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
                    $"Mean reversion requires parameter '{expected.Name}'.",
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
