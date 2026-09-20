namespace Trading.Domain.Universe;

/// <summary>
/// What an instrument's current condition permits.
/// </summary>
/// <remarks>
/// Ordered by severity. Reduction is never blocked at this level: an operator
/// or strategy must always be able to close exposure it already holds, even
/// when the instrument has degraded. Blocking exits would trap capital in a
/// deteriorating market, which is more dangerous than the degradation itself.
/// </remarks>
public enum ExposureDirective
{
    /// <summary>Entries, increases, and reductions are all permitted.</summary>
    AllowIncrease = 0,

    /// <summary>
    /// No new entries and no increases. Reducing or closing existing exposure
    /// is permitted.
    /// </summary>
    ReduceOnly = 1,

    /// <summary>
    /// Only a full close is permitted. Partial adjustments that could be
    /// mis-sized against unknown trading rules are not.
    /// </summary>
    CloseOnly = 2
}

public sealed record ExposureDecision(
    ExposureDirective Directive,
    string Reason)
{
    public bool PermitsNewExposure => Directive == ExposureDirective.AllowIncrease;

    /// <summary>
    /// Reduction is permitted under every directive. This is deliberate and
    /// is covered by test: exposure must always be closable.
    /// </summary>
    public static bool PermitsReduction => true;

    public bool PermitsPartialReduction => Directive != ExposureDirective.CloseOnly;
}

/// <summary>
/// Decides what an instrument's condition currently permits.
/// </summary>
/// <remarks>
/// Evaluated in strict severity order so the most serious condition is always
/// the one reported. Every unknown fails closed: absent measurements, an
/// unknown exchange status, or stale evidence all degrade exposure rather
/// than being assumed acceptable.
///
/// This is a platform ceiling. A user's own configuration can only be
/// stricter; it is applied elsewhere and can never raise this result.
/// </remarks>
public sealed class InstrumentDegradationPolicy
{
    private readonly TimeSpan _maximumEvidenceAge;

    public InstrumentDegradationPolicy(TimeSpan maximumEvidenceAge)
    {
        if (maximumEvidenceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEvidenceAge), "A positive maximum evidence age is required.");
        }

        _maximumEvidenceAge = maximumEvidenceAge;
    }

    public ExposureDecision Evaluate(
        Instrument instrument,
        InstrumentMetrics? metrics,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        // Delisted. Only an exit remains meaningful, and trading rules may no
        // longer be reliable, so a partial adjustment is not permitted.
        if (instrument.State == InstrumentState.Removed)
        {
            return new ExposureDecision(
                ExposureDirective.CloseOnly,
                "The instrument has been removed from the exchange.");
        }

        if (!instrument.IsPresentOnExchange)
        {
            return new ExposureDecision(
                ExposureDirective.CloseOnly,
                "The instrument is not present in the exchange catalogue.");
        }

        if (!instrument.IsTradingOnExchange)
        {
            return new ExposureDecision(
                ExposureDirective.CloseOnly,
                $"The exchange reports status {instrument.ExchangeStatus ?? "unknown"}, not TRADING.");
        }

        // Without trading rules an order cannot be correctly priced or sized,
        // so a partial reduction could be silently rejected or rounded into a
        // materially different order. Only a full close is safe.
        if (!instrument.FiltersLoaded)
        {
            return new ExposureDecision(
                ExposureDirective.CloseOnly,
                "Exchange trading rules are not loaded, so orders cannot be correctly sized.");
        }

        if (instrument.State == InstrumentState.Suspended)
        {
            return new ExposureDecision(
                ExposureDirective.ReduceOnly,
                $"The instrument is suspended: {instrument.SuspensionReason ?? "no reason recorded"}.");
        }

        if (instrument.AssetClass != AssetClass.Cryptocurrency)
        {
            return new ExposureDecision(
                ExposureDirective.ReduceOnly,
                $"Asset class {instrument.AssetClass} is outside the permitted universe.");
        }

        if (metrics is null)
        {
            return new ExposureDecision(
                ExposureDirective.ReduceOnly,
                "No current measurements are available for this instrument.");
        }

        if (metrics.IsStale(nowUtc, _maximumEvidenceAge))
        {
            return new ExposureDecision(
                ExposureDirective.ReduceOnly,
                $"Measurements from {metrics.ComputedAtUtc:O} are stale.");
        }

        if (!metrics.IsDataHealthy)
        {
            return new ExposureDecision(
                ExposureDirective.ReduceOnly,
                $"Data health is degraded: {metrics.DataGapCount} gap(s), "
                + $"{metrics.StaleEventCount} stale event(s).");
        }

        return new ExposureDecision(
            ExposureDirective.AllowIncrease,
            "The instrument is trading, classified, has loaded rules, and has current healthy data.");
    }
}
