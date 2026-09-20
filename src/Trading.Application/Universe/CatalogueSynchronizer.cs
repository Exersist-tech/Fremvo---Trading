using Trading.Domain.Universe;
using Trading.Exchanges.Abstractions.Catalogue;

namespace Trading.Application.Universe;

/// <summary>
/// What a synchronisation did to one instrument.
/// </summary>
public enum CatalogueSyncOutcome
{
    None = 0,
    Observed = 1,
    Discovered = 2,
    SuspendedAsAbsent = 3,
    AbsenceNotActedOn = 4,
    FiltersCleared = 5,
    Skipped = 6
}

public sealed record CatalogueSyncChange(
    string ExchangeSymbol,
    CatalogueSyncOutcome Outcome,
    string Detail);

public sealed record CatalogueSyncReport(
    string ExchangeName,
    DateTimeOffset SynchronisedAtUtc,
    bool SnapshotWasComplete,
    IReadOnlyList<CatalogueSyncChange> Changes)
{
    public int CountOf(CatalogueSyncOutcome outcome) =>
        Changes.Count(change => change.Outcome == outcome);
}

/// <summary>
/// Applies an exchange catalogue snapshot to the tracked instruments.
/// </summary>
/// <remarks>
/// Synchronisation never grants eligibility. It only records what the
/// exchange currently reports, and it withdraws capability when the exchange
/// stops reporting an instrument as usable.
/// </remarks>
public sealed class CatalogueSynchronizer
{
    private readonly AssetClassifier _classifier;

    public CatalogueSynchronizer(AssetClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
    }

    /// <summary>
    /// Reconciles the known instruments against a snapshot.
    /// </summary>
    /// <param name="known">
    /// The instruments already tracked. Mutated in place.
    /// </param>
    /// <param name="snapshot">The catalogue snapshot.</param>
    /// <param name="discovered">
    /// Receives instruments present on the exchange but not yet tracked. They
    /// are created <see cref="InstrumentState.Tracked"/> and confer nothing.
    /// </param>
    /// <param name="idFactory">Supplies ids for discovered instruments.</param>
    public CatalogueSyncReport Synchronize(
        IReadOnlyCollection<Instrument> known,
        InstrumentCatalogueSnapshot snapshot,
        ICollection<Instrument> discovered,
        Func<string, Guid> idFactory)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(idFactory);

        var changes = new List<CatalogueSyncChange>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in known)
        {
            if (instrument.State == InstrumentState.Removed)
            {
                changes.Add(new CatalogueSyncChange(
                    instrument.ExchangeSymbol,
                    CatalogueSyncOutcome.Skipped,
                    "Removed instruments are never reinstated by a synchronisation."));
                continue;
            }

            var entry = snapshot.Find(instrument.ExchangeSymbol);
            if (entry is not null)
            {
                matched.Add(entry.ExchangeSymbol);
                changes.Add(Apply(instrument, entry, snapshot.RetrievedAtUtc));
                continue;
            }

            if (!snapshot.IsComplete)
            {
                // A partial snapshot proves nothing about absence. Acting on
                // it would suspend the whole universe on a transport fault.
                changes.Add(new CatalogueSyncChange(
                    instrument.ExchangeSymbol,
                    CatalogueSyncOutcome.AbsenceNotActedOn,
                    $"Snapshot incomplete ({snapshot.IncompletenessReason}); absence not treated as delisting."));
                continue;
            }

            instrument.MarkAbsentFromCatalogue(
                snapshot.RetrievedAtUtc,
                "Not present in a complete exchange catalogue synchronisation.");
            instrument.ClearFilters();

            changes.Add(new CatalogueSyncChange(
                instrument.ExchangeSymbol,
                CatalogueSyncOutcome.SuspendedAsAbsent,
                "Suspended and filters cleared; records preserved."));
        }

        foreach (var entry in snapshot.Entries)
        {
            if (matched.Contains(entry.ExchangeSymbol))
            {
                continue;
            }

            var instrument = Instrument.CreateFromCatalogue(
                idFactory(entry.ExchangeSymbol),
                snapshot.ExchangeName,
                entry.ExchangeSymbol,
                entry.BaseAsset,
                entry.QuoteAsset,
                _classifier.Classify(entry.BaseAsset));

            Apply(instrument, entry, snapshot.RetrievedAtUtc);
            discovered.Add(instrument);

            changes.Add(new CatalogueSyncChange(
                entry.ExchangeSymbol,
                CatalogueSyncOutcome.Discovered,
                "Discovered on the exchange and tracked; no eligibility granted."));
        }

        return new CatalogueSyncReport(
            snapshot.ExchangeName, snapshot.RetrievedAtUtc, snapshot.IsComplete, changes);
    }

    /// <summary>
    /// Maps the connector's neutral status onto the domain's neutral status.
    /// </summary>
    /// <remarks>
    /// The two enums are deliberately separate types so the domain does not
    /// depend on the exchange abstraction package. The mapping is exhaustive
    /// and anything unmatched becomes
    /// <see cref="InstrumentTradingStatus.Unknown"/>, which is never tradable.
    /// </remarks>
    private static InstrumentTradingStatus MapTradingStatus(CatalogueTradingStatus status) => status switch
    {
        CatalogueTradingStatus.Trading => InstrumentTradingStatus.Trading,
        CatalogueTradingStatus.LimitOnly => InstrumentTradingStatus.LimitOnly,
        CatalogueTradingStatus.PostOnly => InstrumentTradingStatus.PostOnly,
        CatalogueTradingStatus.ReduceOnly => InstrumentTradingStatus.ReduceOnly,
        CatalogueTradingStatus.CancelOnly => InstrumentTradingStatus.CancelOnly,
        CatalogueTradingStatus.Halted => InstrumentTradingStatus.Halted,
        CatalogueTradingStatus.Delisted => InstrumentTradingStatus.Delisted,
        _ => InstrumentTradingStatus.Unknown
    };

    private CatalogueSyncChange Apply(
        Instrument instrument,
        InstrumentCatalogueEntry entry,
        DateTimeOffset observedAtUtc)
    {
        instrument.ObserveInCatalogue(
            MapTradingStatus(entry.Status),
            entry.ExchangeStatusRaw,
            entry.Permissions,
            observedAtUtc,
            entry.OnboardUtc);

        // The classification may change when an asset is reviewed or when the
        // exchange renames it. Re-applying it here keeps an asset that has
        // left the research universe from lingering as tradable.
        instrument.Reclassify(_classifier.Classify(instrument.BaseAsset), observedAtUtc);

        if (entry.HasCompleteFilters)
        {
            instrument.MarkFiltersLoaded(observedAtUtc);
            return new CatalogueSyncChange(
                entry.ExchangeSymbol,
                CatalogueSyncOutcome.Observed,
                $"Status {entry.Status}; complete trading rules loaded.");
        }

        // Incomplete rules must not leave a stale "filters loaded" timestamp
        // behind: order sizing would then run against rules we do not have.
        instrument.ClearFilters();
        return new CatalogueSyncChange(
            entry.ExchangeSymbol,
            CatalogueSyncOutcome.FiltersCleared,
            $"Status {entry.Status}; trading rules incomplete, filters cleared.");
    }
}
