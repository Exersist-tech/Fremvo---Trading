using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;

namespace Trading.Application.Universe;

/// <summary>
/// One instrument as shown to an administrator, with the reason for its
/// current standing.
/// </summary>
/// <remarks>
/// Carries no credentials, no account identifiers, and no user data — this is
/// public market metadata plus the platform's own decision, so it is safe to
/// render. Every row states <em>why</em>: an operator must never be left to
/// guess why an instrument is unavailable, because guessing invites
/// overriding the gate rather than fixing the evidence.
/// </remarks>
public sealed record UniverseInstrumentView(
    Guid InstrumentId,
    string ExchangeName,
    string ExchangeSymbol,
    string BaseAsset,
    string QuoteAsset,
    string AssetClass,
    string State,
    bool IsConfiguredSeed,
    bool IsPresentOnExchange,
    string ExchangeStatus,
    bool FiltersLoaded,
    double? ListingAgeDays,
    string ListingRestriction,
    string ExposureDirective,
    string ExposureReason,
    bool PermitsNewExposure,
    bool PermitsReduction,
    bool Eligible,
    string Explanation,
    IReadOnlyList<UniverseGateView> Gates);

public sealed record UniverseGateView(
    string Gate,
    bool Passed,
    string Detail,
    decimal? MeasuredValue,
    decimal? Threshold);

/// <summary>
/// Builds the administrator view of the instrument universe.
/// </summary>
public sealed class UniverseAdminQueryService
{
    private readonly InstrumentEligibilityEvaluator _evaluator;
    private readonly InstrumentDegradationPolicy _degradationPolicy;
    private readonly NewListingPolicy _listingPolicy;
    private readonly EligibilityThresholds _thresholds;
    private readonly IUniverseEvidenceSource _evidence;
    private readonly TimeProvider _timeProvider;

    public UniverseAdminQueryService(
        InstrumentEligibilityEvaluator evaluator,
        InstrumentDegradationPolicy degradationPolicy,
        NewListingPolicy listingPolicy,
        EligibilityThresholds thresholds,
        IUniverseEvidenceSource evidence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(degradationPolicy);
        ArgumentNullException.ThrowIfNull(listingPolicy);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _evaluator = evaluator;
        _degradationPolicy = degradationPolicy;
        _listingPolicy = listingPolicy.Stricter(NewListingPolicy.PlatformFloor);
        _thresholds = thresholds;
        _evidence = evidence;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<UniverseInstrumentView>> GetAsync(
        IReadOnlyCollection<Instrument> instruments,
        EligibilityPurpose purpose,
        CandleInterval interval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        var nowUtc = _timeProvider.GetUtcNow();
        var scope = new EligibilityScope(purpose, interval, TradingProductType.Spot);
        var views = new List<UniverseInstrumentView>(instruments.Count);

        foreach (var instrument in instruments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metrics = await _evidence
                .GetMetricsAsync(instrument.Id, interval, cancellationToken)
                .ConfigureAwait(false);

            var history = await _evidence
                .GetAvailableHistoryCandlesAsync(instrument.Id, interval, cancellationToken)
                .ConfigureAwait(false);

            var evaluation = _evaluator.Evaluate(
                instrument,
                scope,
                metrics,
                _thresholds,
                new EligibilityInputs(history, StrategyApproved: true, "Template approval checked separately."),
                nowUtc);

            var exposure = _degradationPolicy.Evaluate(instrument, metrics, nowUtc);
            var listingAge = instrument.ListingAge(nowUtc);
            var listingPermits = _listingPolicy.Permits(purpose, listingAge);

            views.Add(new UniverseInstrumentView(
                instrument.Id,
                instrument.ExchangeName,
                instrument.ExchangeSymbol,
                instrument.BaseAsset,
                instrument.QuoteAsset,
                instrument.AssetClass.ToString(),
                instrument.State.ToString(),
                instrument.IsConfiguredSeed,
                instrument.IsPresentOnExchange,
                instrument.ExchangeStatus ?? "unknown",
                instrument.FiltersLoaded,
                listingAge?.TotalDays,
                _listingPolicy.Restriction(listingAge).ToString(),
                exposure.Directive.ToString(),
                exposure.Reason,
                exposure.PermitsNewExposure,
                ExposureDecision.PermitsReduction,
                evaluation.Passed && listingPermits,
                listingPermits
                    ? evaluation.Explain()
                    : $"{evaluation.Explain()} {_listingPolicy.Explain(purpose, listingAge)}",
                evaluation.GateResults
                    .Select(gate => new UniverseGateView(
                        gate.Gate.ToString(), gate.Passed, gate.Detail, gate.MeasuredValue, gate.Threshold))
                    .ToList()));
        }

        return views;
    }
}
