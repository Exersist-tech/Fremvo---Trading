using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Strategies;
using Trading.Indicators;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored, versioned, closed-candle Bollinger research model.
/// Its bounded ranging check is local to this family; it is not a regime classifier.
/// </summary>
public sealed class BollingerMeanReversionResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string BollingerPeriodParameter = "bollingerPeriod";
    private const string StandardDeviationMultiplierParameter = "standardDeviationMultiplier";
    private const string RangeEmaPeriodParameter = "rangeEmaPeriod";
    private const string MaximumEmaSlopePercentParameter = "maximumEmaSlopePercent";
    private const string MaximumBandWidthPercentParameter = "maximumBandWidthPercent";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(BollingerPeriodParameter, 10m, 40m, 20m, "Completed candles used for Bollinger bands.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(StandardDeviationMultiplierParameter, 1.5m, 3m, 2m, "Decimal standard-deviation multiplier for Bollinger bands.", valueType: StrategyParameterValueType.Numeric),
            new StrategyParameterDefinition(RangeEmaPeriodParameter, 5m, 20m, 10m, "Completed higher-timeframe EMA period used only by this family's ranging check.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(MaximumEmaSlopePercentParameter, 0.05m, 0.50m, 0.20m, "Maximum absolute one-candle higher-timeframe EMA slope percentage for this family's ranging check.", valueType: StrategyParameterValueType.Numeric),
            new StrategyParameterDefinition(MaximumBandWidthPercentParameter, 1m, 10m, 5m, "Maximum higher-timeframe Bollinger width percentage for this family's ranging check.", valueType: StrategyParameterValueType.Numeric)
        ]);

    public BollingerMeanReversionResearchModel()
        : base(
            "bollinger-mean-reversion-v1",
            "Bollinger Mean Reversion Research",
            TradingProductType.Spot,
            "Bollinger mean-reversion research using only completed, safe, as-of-aligned candles.",
            "bands: 10-40, 1.5-3.0 deviations; range EMA: 5-20; max EMA slope: 0.05-0.50%; max band width: 1-10%",
            "A local, bounded ranging check requires limited higher-timeframe EMA slope and band width; signal closes at or beyond a Bollinger boundary are observed for mean reversion.",
            "Requires accepted rejection gates, configured Regime/Signal/Execution roles, sufficient completed warmup, and safe as-of-aligned candles.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("bollinger-mean-reversion-v1");

    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        return Neutral(input, "Unavailable: accepted rejection-gate evidence is required before Bollinger research evaluation.");
    }

    public StrategyAnalysisProposal Evaluate(BollingerMeanReversionEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var input = evaluation.EvaluationInput;
        ValidateInput(input);

        if (!evaluation.HasAcceptedGatesAt(input.AsOfUtc))
        {
            return Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle.");
        }

        var bollingerPeriod = Period(input, BollingerPeriodParameter);
        var multiplier = input.Parameters.GetDecimal(StandardDeviationMultiplierParameter);
        var rangeEmaPeriod = Period(input, RangeEmaPeriodParameter);
        var requiredRegime = Math.Max(bollingerPeriod, rangeEmaPeriod + 1);
        if (input.Regime.ClosedCandles.Count < requiredRegime
            || input.Signal.ClosedCandles.Count < bollingerPeriod
            || input.Execution.ClosedCandles.Count < 1)
        {
            return Neutral(
                input,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unavailable: requires {requiredRegime} regime, {bollingerPeriod} signal, and 1 execution completed candle."));
        }

        var regimeBands = Bands(input.Regime.ClosedCandles, bollingerPeriod, multiplier);
        if (regimeBands.Middle <= 0m)
        {
            return Neutral(input, "Unavailable: the ranging check cannot calculate percentages from a non-positive completed Bollinger middle band.");
        }

        var regimeEma = Ema(input.Regime.ClosedCandles, rangeEmaPeriod);
        var previousRegimeEma = Ema(input.Regime.ClosedCandles.Take(input.Regime.ClosedCandles.Count - 1).ToArray(), rangeEmaPeriod);
        if (previousRegimeEma <= 0m)
        {
            return Neutral(input, "Unavailable: the ranging check cannot calculate EMA slope from a non-positive completed EMA.");
        }

        var slopePercent = decimal.Abs((regimeEma - previousRegimeEma) / previousRegimeEma) * 100m;
        var widthPercent = ((regimeBands.Upper - regimeBands.Lower) / regimeBands.Middle) * 100m;
        var maximumSlope = input.Parameters.GetDecimal(MaximumEmaSlopePercentParameter);
        var maximumWidth = input.Parameters.GetDecimal(MaximumBandWidthPercentParameter);
        if (slopePercent > maximumSlope || widthPercent > maximumWidth)
        {
            return Neutral(
                input,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No condition: this family's bounded ranging check failed (EMA slope {slopePercent:F4}% <= {maximumSlope:F4}%, band width {widthPercent:F4}% <= {maximumWidth:F4}%)."));
        }

        var signalBands = Bands(input.Signal.ClosedCandles, bollingerPeriod, multiplier);
        var close = input.Signal.ClosedCandles[^1].Close;
        if (close <= signalBands.Lower)
        {
            return Observation(input, StrategyAnalysisDirection.Bullish, "lower", close, signalBands);
        }

        if (close >= signalBands.Upper)
        {
            return Observation(input, StrategyAnalysisDirection.Bearish, "upper", close, signalBands);
        }

        return Neutral(input, "No condition: the completed signal close is inside the Bollinger boundaries.");
    }

    private StrategyAnalysisProposal Observation(
        StrategyEvaluationInput input,
        StrategyAnalysisDirection direction,
        string boundary,
        decimal close,
        BollingerBandsValue bands) =>
        new(
            TemplateId,
            input.AsOfUtc,
            direction,
            0.40m,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Completed Bollinger mean-reversion observation: signal close {close:F4} is at or beyond the {boundary} boundary {((boundary == "lower") ? bands.Lower : bands.Upper):F4}; analysis only, not a trade instruction."));

    private static BollingerBandsValue Bands(IReadOnlyList<Candle> candles, int period, decimal multiplier) =>
        new BollingerBandsCalculator(period, multiplier).Calculate(candles).Value
        ?? throw new InvalidOperationException("Bollinger warmup was checked before calculation.");

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
            throw new ArgumentException("Bollinger mean reversion requires a distinct higher-timeframe regime role.", nameof(input));
        }
    }

    private static void ValidateParameters(StrategyParameterSet parameters)
    {
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("Bollinger mean reversion accepts only its platform-defined parameters.", nameof(parameters));
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
                throw new ArgumentException($"Bollinger mean reversion requires parameter '{expected.Name}'.", nameof(parameters), exception);
            }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum
                || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required
                || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(parameters));
            }
        }
    }
}

/// <summary>
/// Couples immutable market data with recorded governance gates and has no
/// order, account, sizing, leverage, or execution capability.
/// </summary>
public sealed class BollingerMeanReversionEvaluationInput
{
    public BollingerMeanReversionEvaluationInput(
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
