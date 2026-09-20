using Trading.Domain.Market;
using Trading.Domain.Universe;

namespace Trading.Application.Universe;

/// <summary>
/// Supplies the evidence needed to recalculate one instrument's eligibility.
/// </summary>
/// <remarks>
/// Returning <c>null</c> metrics is a valid answer meaning "not measured",
/// and it must degrade eligibility rather than be treated as a pass.
/// </remarks>
public interface IUniverseEvidenceSource
{
    Task<InstrumentMetrics?> GetMetricsAsync(
        Guid instrumentId,
        CandleInterval interval,
        CancellationToken cancellationToken);

    Task<int> GetAvailableHistoryCandlesAsync(
        Guid instrumentId,
        CandleInterval interval,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a recalculation did to one scope of one instrument.
/// </summary>
public enum RecalculationOutcome
{
    None = 0,
    Granted = 1,
    Retained = 2,
    Revoked = 3,
    StillIneligible = 4,
    BlockedByListingAge = 5,
    Failed = 6
}

public sealed record RecalculationChange(
    Guid InstrumentId,
    string ExchangeSymbol,
    EligibilityScope Scope,
    RecalculationOutcome Outcome,
    ExposureDirective Directive,
    string Explanation);

public sealed record RecalculationReport(
    DateTimeOffset RecalculatedAtUtc,
    IReadOnlyList<RecalculationChange> Changes)
{
    public int CountOf(RecalculationOutcome outcome) =>
        Changes.Count(change => change.Outcome == outcome);
}

/// <summary>
/// Recalculates instrument eligibility from current evidence.
/// </summary>
/// <remarks>
/// <para>
/// Recalculation is the mechanism by which eligibility decays. An instrument
/// that stops meeting a gate loses the grant automatically; nothing has to
/// notice and intervene. Running this on a schedule is therefore a safety
/// control, not a convenience.
/// </para>
/// <para>
/// It only ever grants up to <see cref="EligibilityPurpose.Paper"/>. Test and
/// live purposes require an explicit, audited administrator approval and are
/// never produced automatically — but they <em>are</em> revoked automatically
/// when their evidence fails, because withdrawing capability must never need
/// an approval.
/// </para>
/// <para>
/// A failure evaluating one instrument never stops the others, and never
/// leaves a grant standing on unverified evidence.
/// </para>
/// </remarks>
public sealed class UniverseRecalculationService
{
    /// <summary>The highest purpose this service may grant without a human.</summary>
    public const EligibilityPurpose HighestAutomaticPurpose = EligibilityPurpose.Paper;

    private readonly InstrumentEligibilityEvaluator _evaluator;
    private readonly InstrumentDegradationPolicy _degradationPolicy;
    private readonly NewListingPolicy _listingPolicy;
    private readonly EligibilityThresholds _configuredThresholds;
    private readonly IUniverseEvidenceSource _evidence;
    private readonly TimeProvider _timeProvider;

    public UniverseRecalculationService(
        InstrumentEligibilityEvaluator evaluator,
        InstrumentDegradationPolicy degradationPolicy,
        NewListingPolicy listingPolicy,
        EligibilityThresholds configuredThresholds,
        IUniverseEvidenceSource evidence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(degradationPolicy);
        ArgumentNullException.ThrowIfNull(listingPolicy);
        ArgumentNullException.ThrowIfNull(configuredThresholds);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _evaluator = evaluator;
        _degradationPolicy = degradationPolicy;
        _listingPolicy = listingPolicy.Stricter(NewListingPolicy.PlatformFloor);
        _configuredThresholds = configuredThresholds;
        _evidence = evidence;
        _timeProvider = timeProvider;
    }

    public async Task<RecalculationReport> RecalculateAsync(
        IReadOnlyCollection<Instrument> instruments,
        IReadOnlyDictionary<Guid, InstrumentEligibility> eligibility,
        IReadOnlyCollection<EligibilityScope> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(eligibility);
        ArgumentNullException.ThrowIfNull(scopes);

        var nowUtc = _timeProvider.GetUtcNow();
        var changes = new List<RecalculationChange>();

        foreach (var instrument in instruments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!eligibility.TryGetValue(instrument.Id, out var instrumentEligibility))
            {
                continue;
            }

            foreach (var scope in scopes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                changes.Add(await RecalculateScopeAsync(
                    instrument, instrumentEligibility, scope, nowUtc, cancellationToken).ConfigureAwait(false));
            }
        }

        return new RecalculationReport(nowUtc, changes);
    }

    private async Task<RecalculationChange> RecalculateScopeAsync(
        Instrument instrument,
        InstrumentEligibility eligibility,
        EligibilityScope scope,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ExposureDirective directive;

        try
        {
            directive = _degradationPolicy.Evaluate(instrument, null, nowUtc).Directive;

            var metrics = await _evidence
                .GetMetricsAsync(instrument.Id, scope.Interval, cancellationToken)
                .ConfigureAwait(false);

            directive = _degradationPolicy.Evaluate(instrument, metrics, nowUtc).Directive;

            var history = await _evidence
                .GetAvailableHistoryCandlesAsync(instrument.Id, scope.Interval, cancellationToken)
                .ConfigureAwait(false);

            var evaluation = _evaluator.Evaluate(
                instrument,
                scope,
                metrics,
                _configuredThresholds,
                new EligibilityInputs(history, StrategyApproved: true, "Scope requested by an approved template."),
                nowUtc);

            if (!evaluation.Passed)
            {
                return Revoke(instrument, eligibility, scope, directive, evaluation.Explain(), nowUtc);
            }

            var listingAge = instrument.ListingAge(nowUtc);
            if (!_listingPolicy.Permits(scope.Purpose, listingAge))
            {
                return Revoke(
                    instrument,
                    eligibility,
                    scope,
                    directive,
                    _listingPolicy.Explain(scope.Purpose, listingAge),
                    nowUtc,
                    RecalculationOutcome.BlockedByListingAge);
            }

            if (scope.Purpose > HighestAutomaticPurpose)
            {
                // Evidence still supports it, but this purpose is never
                // granted without an administrator. Leave it exactly as it is.
                return new RecalculationChange(
                    instrument.Id, instrument.ExchangeSymbol, scope,
                    IsGranted(eligibility, scope, nowUtc)
                        ? RecalculationOutcome.Retained
                        : RecalculationOutcome.StillIneligible,
                    directive,
                    $"{scope.Purpose} requires an explicit administrator approval and is never granted automatically.");
            }

            if (directive != ExposureDirective.AllowIncrease)
            {
                return Revoke(
                    instrument, eligibility, scope, directive,
                    $"Degraded to {directive}; automatic eligibility withheld.", nowUtc);
            }

            var alreadyGranted = IsGranted(eligibility, scope, nowUtc);

            // Grants are additive and each requires its prerequisite, so the
            // chain is granted in order up to the requested purpose. The
            // chain stops at Paper; it can never reach a live purpose.
            foreach (var purpose in ChainTo(scope.Purpose))
            {
                eligibility.Grant(
                    scope with { Purpose = purpose },
                    nowUtc,
                    evidenceAsOfUtc: nowUtc,
                    _configuredThresholds.MaximumEvidenceAge);
            }

            return new RecalculationChange(
                instrument.Id, instrument.ExchangeSymbol, scope,
                alreadyGranted ? RecalculationOutcome.Retained : RecalculationOutcome.Granted,
                directive,
                evaluation.Explain());
        }
#pragma warning disable CA1031 // One instrument must never stop the rest.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            // The evidence could not be established, so the grant cannot be
            // justified. Revoking is the only safe response to not knowing.
            return Revoke(
                instrument, eligibility, scope, ExposureDirective.ReduceOnly,
                $"Recalculation failed and eligibility cannot be justified: {exception.GetType().Name}.",
                nowUtc,
                RecalculationOutcome.Failed);
        }
    }

    private static RecalculationChange Revoke(
        Instrument instrument,
        InstrumentEligibility eligibility,
        EligibilityScope scope,
        ExposureDirective directive,
        string explanation,
        DateTimeOffset nowUtc,
        RecalculationOutcome failureOutcome = RecalculationOutcome.Revoked)
    {
        var wasGranted = eligibility.HasGrant(scope);

        // Revocation cascades to every dependent purpose, including the live
        // purposes this service may not grant. Withdrawing capability never
        // requires an approval.
        eligibility.Revoke(scope);

        return new RecalculationChange(
            instrument.Id, instrument.ExchangeSymbol, scope,
            wasGranted ? failureOutcome : RecalculationOutcome.StillIneligible,
            directive,
            explanation);
    }

    private bool IsGranted(InstrumentEligibility eligibility, EligibilityScope scope, DateTimeOffset nowUtc) =>
        eligibility.IsGranted(scope, nowUtc, _configuredThresholds.MaximumEvidenceAge);

    /// <summary>
    /// The prerequisite chain from Research up to and including the requested
    /// purpose. Never extends past <see cref="HighestAutomaticPurpose"/>.
    /// </summary>
    private static IEnumerable<EligibilityPurpose> ChainTo(EligibilityPurpose purpose)
    {
        for (var step = EligibilityPurpose.Research; step <= purpose; step++)
        {
            if (step > HighestAutomaticPurpose)
            {
                yield break;
            }

            yield return step;
        }
    }
}
