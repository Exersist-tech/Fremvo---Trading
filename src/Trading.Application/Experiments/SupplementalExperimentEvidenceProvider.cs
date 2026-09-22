using Trading.Domain.Market;
using Trading.Domain.Sessions;
using Trading.Domain.Experiments;
using Trading.MarketData.Experiments;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.Application.Experiments;

/// <summary>
/// Supplies only the additional, platform-fixed evidence required by the four multi-input
/// paper research families. Implementations must read persisted evidence only.
/// </summary>
public interface ISupplementalExperimentEvidenceProvider
{
    Task<ExperimentAnalysisResult?> EvaluateAsync(
        string familyId,
        ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed default: supplemental families remain unavailable unless explicitly wired.</summary>
public sealed class UnconfiguredSupplementalExperimentEvidenceProvider : ISupplementalExperimentEvidenceProvider
{
    public Task<ExperimentAnalysisResult?> EvaluateAsync(string familyId, ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance, CancellationToken cancellationToken = default) =>
        Task.FromResult<ExperimentAnalysisResult?>(null);
}

/// <summary>
/// Fixed paper-only evidence provider. It has no user parameters, network, credential, order, or
/// allocation surface. Cross-sectional evaluations always retain all three required symbols.
/// </summary>
public sealed class PlatformSupplementalExperimentEvidenceProvider : ISupplementalExperimentEvidenceProvider
{
    private static readonly string[] Universe = ["BTC/USD", "ETH/USD", "SOL/USD", "XRP/EUR", "TRX/EUR", "DOGE/EUR", "ADA/EUR"];
    private readonly IExperimentCandleSeriesSource _candles;

    public PlatformSupplementalExperimentEvidenceProvider(IExperimentCandleSeriesSource candles) =>
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));

    public async Task<ExperimentAnalysisResult?> EvaluateAsync(
        string familyId, ExperimentCandleSeries primarySeries, ExperimentResearchProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primarySeries);
        ArgumentNullException.ThrowIfNull(provenance);
        if (familyId is not ("platform.cross-sectional-momentum-rotation"
            or "platform.relative-strength-pullback-rotation"
            or "platform.session-conditioned-breakout"
            or "platform.regime-switching-ensemble"))
        {
            return null;
        }

        if (!HasExactSafeSeries(primarySeries))
        {
            return ExperimentAnalysisResult.Blocked(
                "Supplemental evidence requires a safe chronological series closed exactly at the UTC as-of.");
        }
        return familyId switch
        {
            "platform.cross-sectional-momentum-rotation" => await CrossAsync(primarySeries, provenance, false, cancellationToken).ConfigureAwait(false),
            "platform.relative-strength-pullback-rotation" => await CrossAsync(primarySeries, provenance, true, cancellationToken).ConfigureAwait(false),
            "platform.session-conditioned-breakout" => Session(primarySeries, provenance),
            "platform.regime-switching-ensemble" => Regime(primarySeries, provenance),
            _ => null
        };
    }

    private async Task<ExperimentAnalysisResult> CrossAsync(ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance, bool relativeStrength, CancellationToken cancellationToken)
    {
        const int history = 91;
        var members = new List<CrossSectionalMomentumUniverseMember>(Universe.Length);
        foreach (var symbol in Universe)
        {
            var result = await _candles.GetClosedSeriesAsync(
                new ExperimentCandleSeriesRequest(symbol, primarySeries.Interval, primarySeries.AsOfUtc, history), cancellationToken).ConfigureAwait(false);
            if (!result.IsAvailable || result.Series is null || result.Series.AsOfUtc != primarySeries.AsOfUtc
                || result.Series.Candles.Count != history || result.Series.Candles[^1].CloseTimeUtc != primarySeries.AsOfUtc)
            {
                return ExperimentAnalysisResult.Blocked(
                    $"Required fixed universe member '{symbol}' is unavailable, incomplete, or not closed exactly at the UTC as-of.");
            }
            members.Add(new CrossSectionalMomentumUniverseMember(
                Instrument(symbol), symbol, true, Trading.Domain.Universe.InstrumentTradingStatus.Trading,
                primarySeries.AsOfUtc, result.Series.Candles));
        }

        var dataset = new CrossSectionalMomentumDataset("platform-fixed-paper-universe-v2", primarySeries.AsOfUtc, primarySeries.Interval, members);
        if (!relativeStrength)
        {
            var model = new CrossSectionalMomentumRotationResearchModel();
            var result = model.Evaluate(new CrossSectionalMomentumRotationEvaluationInput(
                dataset, new StrategyParameterSet(model.ParameterDefinitions,
                    new Dictionary<string, StrategyParameterValue>
                    {
                        ["lookbackPeriods"] = StrategyParameterValue.WholeNumber(90),
                        ["topCount"] = StrategyParameterValue.WholeNumber(1)
                    }), provenance.GateEvaluation));
            if (result.Status == CrossSectionalMomentumResearchStatus.Blocked) return ExperimentAnalysisResult.Blocked(result.Rationale);
            return result.Rankings.Single().Symbol == primarySeries.Symbol
                ? ExperimentAnalysisResult.Analyzed(result.Rationale, 1m)
                : ExperimentAnalysisResult.NoCondition($"{result.Rationale} {primarySeries.Symbol} is not the fixed worker candidate.");
        }

        var relative = new RelativeStrengthPullbackRotationResearchModel();
        var evaluation = relative.Evaluate(new RelativeStrengthPullbackRotationEvaluationInput(
            dataset, new StrategyParameterSet(relative.ParameterDefinitions,
                new Dictionary<string, StrategyParameterValue>
                {
                    ["momentumLookbackPeriods"] = StrategyParameterValue.WholeNumber(90),
                    ["pullbackLookbackPeriods"] = StrategyParameterValue.WholeNumber(20),
                    ["minimumPullbackFraction"] = StrategyParameterValue.FromNumeric(.03m),
                    ["maximumPullbackFraction"] = StrategyParameterValue.FromNumeric(.10m)
                }), provenance.GateEvaluation));
        if (evaluation.Status == RelativeStrengthPullbackResearchStatus.Blocked) return ExperimentAnalysisResult.Blocked(evaluation.Rationale);
        return evaluation.Candidate?.Symbol == primarySeries.Symbol
            ? ExperimentAnalysisResult.Analyzed(evaluation.Rationale, 1m)
            : ExperimentAnalysisResult.NoCondition($"{evaluation.Rationale} {primarySeries.Symbol} is not the fixed worker candidate.");
    }

    private static ExperimentAnalysisResult Session(ExperimentCandleSeries series, ExperimentResearchProvenance provenance)
    {
        var model = new SessionConditionedBreakoutResearchModel();
        var input = new StrategyEvaluationInput(model.TemplateId,
            new StrategyParameterSet(model.ParameterDefinitions),
            new StrategyState(model.TemplateId, 0, series.Candles[0].CloseTimeUtc),
            series.Candles);
        var profile = new SessionProfile(new SessionProfileVersionIdentity("platform.new-york-cash-session", 1),
            "Platform New York cash session", "America/New_York", new TimeOnly(9, 30), new TimeOnly(16, 0),
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            SessionCrossMidnightBehavior.SameLocalDay, SessionDstPolicy.FailClosed);
        var output = model.Evaluate(new SessionConditionedBreakoutEvaluationInput(input, provenance.GateEvaluation, profile)).SessionConditioned.Proposal;
        return output.Direction == StrategyAnalysisDirection.Bullish
            ? ExperimentAnalysisResult.Analyzed(output.Rationale, 1m)
            : ExperimentAnalysisResult.NoCondition(output.Rationale);
    }

    private static ExperimentAnalysisResult Regime(ExperimentCandleSeries series, ExperimentResearchProvenance provenance)
    {
        var classification = new DeterministicRegimeClassifier().Evaluate(
            new RegimeClassificationInput(series.Candles, series.AsOfUtc, provenance.GateEvaluation));
        if (classification.Regime == MarketRegime.Unknown)
            return ExperimentAnalysisResult.Blocked(classification.Rationale);
        var model = new RegimeSwitchingEnsembleResearchModel();
        var timeframe = new StrategyTimeframeConfiguration(series.Interval, series.Interval, series.Interval);
        var component = new RegimeSwitchingComponentObservation(
            new StrategyAnalysisProposal(new StrategyTemplateId("ema-trend-continuation-v1"), series.AsOfUtc,
                StrategyAnalysisDirection.Bullish, .5m, "Fixed closed-candle platform component observation."),
            new RegimeSwitchingObservationProvenance("platform-fixed-paper-universe-v2", series.Symbol, timeframe, series.AsOfUtc, true));
        var result = model.Evaluate(new RegimeSwitchingEnsembleEvaluationInput(classification, [component], provenance.GateEvaluation));
        return result.Status == RegimeSwitchingEnsembleStatus.Observed && result.Direction == StrategyAnalysisDirection.Bullish
            ? ExperimentAnalysisResult.Analyzed(result.Rationale, 1m)
            : result.Status == RegimeSwitchingEnsembleStatus.Blocked
                ? ExperimentAnalysisResult.Blocked(result.Rationale)
                : ExperimentAnalysisResult.NoCondition(result.Rationale);
    }

    private static Guid Instrument(string symbol) => symbol switch
    {
        "BTC/USD" => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        "ETH/USD" => Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
        "SOL/USD" => Guid.Parse("55555555-5555-5555-5555-555555555555"),
        "XRP/EUR" => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "TRX/EUR" => Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        "DOGE/EUR" => Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        "ADA/EUR" => Guid.Parse("f0f0f0f0-f0f0-f0f0-f0f0-f0f0f0f0f0f0"),
        _ => throw new InvalidOperationException($"'{symbol}' is not an approved paper-universe symbol.")
    };

    private static bool HasExactSafeSeries(ExperimentCandleSeries series)
    {
        if (series.Candles.Count == 0 || series.Candles[^1].CloseTimeUtc != series.AsOfUtc)
            return false;
        Candle? previous = null;
        foreach (var candle in series.Candles)
        {
            if (candle is null || !candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal
                || candle.CloseTimeUtc > series.AsOfUtc
                || (previous is not null && (candle.OpenTimeUtc <= previous.OpenTimeUtc || candle.CloseTimeUtc <= previous.CloseTimeUtc)))
                return false;
            previous = candle;
        }
        return true;
    }
}
