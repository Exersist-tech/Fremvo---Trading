using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Strategies;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored, versioned, closed-candle Donchian research model.
/// It produces an analysis observation only and cannot initiate trading.
/// </summary>
public sealed class DonchianBreakoutEnsembleResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string ShortChannelPeriodParameter = "shortChannelPeriod";
    private const string MediumChannelPeriodParameter = "mediumChannelPeriod";
    private const string LongChannelPeriodParameter = "longChannelPeriod";
    private const string RequiredChannelConfirmationsParameter = "requiredChannelConfirmations";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(ShortChannelPeriodParameter, 5m, 20m, 10m, "Completed signal candles in the short Donchian channel.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(MediumChannelPeriodParameter, 15m, 40m, 20m, "Completed signal candles in the medium Donchian channel.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(LongChannelPeriodParameter, 30m, 80m, 40m, "Completed signal candles in the long Donchian channel.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(RequiredChannelConfirmationsParameter, 2m, 3m, 2m, "Independent completed-channel breakouts required from the bounded ensemble.", valueType: StrategyParameterValueType.WholeNumber)
        ]);

    public DonchianBreakoutEnsembleResearchModel()
        : base(
            "donchian-breakout-ensemble-v1",
            "Donchian Breakout Ensemble Research",
            TradingProductType.Spot,
            "Multi-channel Donchian breakout research using only completed, safe, as-of-aligned candles.",
            "short channel: 5-20; medium: 15-40; long: 30-80; confirmations: 2-3",
            "Positive completed higher-timeframe channel regime, strict closed-candle upper-channel breaks, and a bounded multi-channel consensus.",
            "Requires accepted rejection gates, configured Regime/Signal/Execution roles, sufficient completed warmup, and safe as-of-aligned candles.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("donchian-breakout-ensemble-v1");

    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    /// <summary>
    /// The generic strategy surface has no governance evidence, so it fails
    /// closed. Call the contextual overload with recorded gate results.
    /// </summary>
    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        return Neutral(input, "Unavailable: accepted rejection-gate evidence is required before Donchian research evaluation.");
    }

    public StrategyAnalysisProposal Evaluate(DonchianBreakoutEnsembleEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var input = evaluation.EvaluationInput;
        ValidateInput(input);

        if (!evaluation.HasAcceptedGatesAt(input.AsOfUtc))
        {
            return Neutral(input, "Unavailable: rejection gates are missing, failed, or not aligned to the completed execution candle.");
        }

        var shortPeriod = Period(input, ShortChannelPeriodParameter);
        var mediumPeriod = Period(input, MediumChannelPeriodParameter);
        var longPeriod = Period(input, LongChannelPeriodParameter);
        var requiredConfirmations = Period(input, RequiredChannelConfirmationsParameter);

        if (input.Regime.ClosedCandles.Count < longPeriod + 1
            || input.Signal.ClosedCandles.Count < longPeriod + 1
            || input.Execution.ClosedCandles.Count < 1)
        {
            return Neutral(
                input,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unavailable: requires {longPeriod + 1} regime and signal completed candles, plus 1 execution completed candle."));
        }

        var regime = ChannelBeforeLatest(input.Regime.ClosedCandles, longPeriod);
        if (input.Regime.ClosedCandles[^1].Close <= regime.Midpoint)
        {
            return Neutral(input, "No condition: the higher-timeframe completed Donchian regime is not positive.");
        }

        var signal = input.Signal.ClosedCandles;
        var channels = new[]
        {
            ChannelBeforeLatest(signal, shortPeriod),
            ChannelBeforeLatest(signal, mediumPeriod),
            ChannelBeforeLatest(signal, longPeriod)
        };
        var confirmations = channels.Count(channel => signal[^1].Close > channel.Upper);
        if (confirmations < requiredConfirmations)
        {
            return Neutral(
                input,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"No condition: {confirmations} of 3 completed-channel confirmations; requires {requiredConfirmations}."));
        }

        var execution = input.Execution.ClosedCandles[^1];
        if (execution.Close <= execution.Open)
        {
            return Neutral(input, "No condition: the completed execution candle does not confirm upward breakout.");
        }

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Bullish,
            0.50m,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Completed Donchian ensemble observation: {confirmations} of 3 strict upper-channel confirmations; signal channel range {channels[^1].Lower:F4}-{channels[^1].Upper:F4}; analysis only, not a trade instruction."));
    }

    private static DonchianChannel ChannelBeforeLatest(IReadOnlyList<Candle> candles, int period)
    {
        var candidateIndex = candles.Count - 1;
        var start = candidateIndex - period;
        if (start < 0)
        {
            throw new InvalidOperationException("Channel warmup was checked before calculation.");
        }

        var upper = decimal.MinValue;
        var lower = decimal.MaxValue;
        for (var index = start; index < candidateIndex; index++)
        {
            upper = Math.Max(upper, candles[index].High);
            lower = Math.Min(lower, candles[index].Low);
        }

        return new DonchianChannel(upper, lower);
    }

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
            throw new ArgumentException("Donchian breakout ensemble requires a distinct higher-timeframe regime role.", nameof(input));
        }
    }

    private static void ValidateParameters(StrategyParameterSet parameters)
    {
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("Donchian breakout ensemble accepts only its platform-defined parameters.", nameof(parameters));
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
                throw new ArgumentException($"Donchian breakout ensemble requires parameter '{expected.Name}'.", nameof(parameters), exception);
            }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum
                || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required
                || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(parameters));
            }
        }

        if (parameters.GetDecimal(ShortChannelPeriodParameter) >= parameters.GetDecimal(MediumChannelPeriodParameter)
            || parameters.GetDecimal(MediumChannelPeriodParameter) >= parameters.GetDecimal(LongChannelPeriodParameter))
        {
            throw new ArgumentException("Donchian channel periods must be strictly increasing.", nameof(parameters));
        }
    }

    private readonly record struct DonchianChannel(decimal Upper, decimal Lower)
    {
        public decimal Midpoint => (Upper + Lower) / 2m;
    }
}

/// <summary>
/// Couples the immutable market snapshot with recorded governance gates.
/// It intentionally cannot carry orders, account data, sizing, or execution data.
/// </summary>
public sealed class DonchianBreakoutEnsembleEvaluationInput
{
    public DonchianBreakoutEnsembleEvaluationInput(
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
