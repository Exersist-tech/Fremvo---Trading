namespace Trading.Domain.Universe;

/// <summary>
/// An exchange-neutral tradable instrument.
/// </summary>
/// <remarks>
/// Identity is the exchange name plus the exchange's own symbol identifier,
/// never a display name: short tickers are reused across projects and
/// exchanges, so a name-based key would silently merge different assets.
///
/// An instrument always begins as <see cref="InstrumentState.Tracked"/>. It
/// never becomes eligible for research, backtesting, paper trading, or live
/// trading as a side effect of being created or synchronised.
/// </remarks>
public sealed class Instrument
{
    private readonly HashSet<string> _permissions;

    private Instrument(
        Guid id,
        string exchangeName,
        string exchangeSymbol,
        string baseAsset,
        string quoteAsset,
        AssetClass assetClass,
        bool isConfiguredSeed)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Instrument id is required.", nameof(id));
        }

        Id = id;
        ExchangeName = Require(exchangeName, nameof(exchangeName));
        ExchangeSymbol = Require(exchangeSymbol, nameof(exchangeSymbol)).ToUpperInvariant();
        BaseAsset = Require(baseAsset, nameof(baseAsset)).ToUpperInvariant();
        QuoteAsset = Require(quoteAsset, nameof(quoteAsset)).ToUpperInvariant();
        AssetClass = assetClass;
        IsConfiguredSeed = isConfiguredSeed;

        State = InstrumentState.Tracked;
        ExchangeStatus = null;
        IsPresentOnExchange = false;
        _permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public Guid Id { get; }

    public string ExchangeName { get; }

    public string ExchangeSymbol { get; }

    public string BaseAsset { get; }

    public string QuoteAsset { get; }

    public AssetClass AssetClass { get; private set; }

    public InstrumentState State { get; private set; }

    /// <summary>
    /// The exchange's own status string, as reported by the catalogue.
    /// Null until the instrument has been seen in a catalogue synchronisation.
    /// </summary>
    public string? ExchangeStatus { get; private set; }

    /// <summary>
    /// False until a catalogue synchronisation has actually observed this
    /// instrument. A configured seed pair that the exchange does not list
    /// stays false, which is visible rather than silently dropped.
    /// </summary>
    public bool IsPresentOnExchange { get; private set; }

    /// <summary>
    /// True when the instrument came from the configured research seed rather
    /// than from the exchange catalogue. Seed membership grants nothing.
    /// </summary>
    public bool IsConfiguredSeed { get; }

    public DateTimeOffset? LastCatalogueSyncUtc { get; private set; }

    public DateTimeOffset? FiltersLoadedAtUtc { get; private set; }

    public DateTimeOffset? FirstObservedCandleUtc { get; private set; }

    public DateTimeOffset? ExchangeOnboardUtc { get; private set; }

    public string? SuspensionReason { get; private set; }

    public IReadOnlyCollection<string> Permissions => _permissions;

    /// <summary>
    /// True when the exchange currently reports the instrument as trading.
    /// </summary>
    public bool IsTradingOnExchange =>
        IsPresentOnExchange &&
        string.Equals(ExchangeStatus, "TRADING", StringComparison.OrdinalIgnoreCase);

    public bool HasSpotPermission => _permissions.Contains("SPOT");

    public bool FiltersLoaded => FiltersLoadedAtUtc is not null;

    /// <summary>
    /// Creates an instrument from the configured research seed. It is
    /// <see cref="InstrumentState.Tracked"/> and not yet known to exist on the
    /// exchange; a catalogue synchronisation must confirm that.
    /// </summary>
    public static Instrument CreateSeed(
        Guid id,
        string exchangeName,
        string exchangeSymbol,
        string baseAsset,
        string quoteAsset,
        AssetClass assetClass) =>
        new(id, exchangeName, exchangeSymbol, baseAsset, quoteAsset, assetClass, isConfiguredSeed: true);

    /// <summary>
    /// Creates an instrument discovered in the exchange catalogue.
    /// </summary>
    public static Instrument CreateFromCatalogue(
        Guid id,
        string exchangeName,
        string exchangeSymbol,
        string baseAsset,
        string quoteAsset,
        AssetClass assetClass) =>
        new(id, exchangeName, exchangeSymbol, baseAsset, quoteAsset, assetClass, isConfiguredSeed: false);

    /// <summary>
    /// Records that a catalogue synchronisation observed this instrument.
    /// </summary>
    public void ObserveInCatalogue(
        string exchangeStatus,
        IEnumerable<string> permissions,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? exchangeOnboardUtc = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (State == InstrumentState.Removed)
        {
            throw new InvalidOperationException(
                "A removed instrument cannot be reinstated by a catalogue synchronisation.");
        }

        ExchangeStatus = Require(exchangeStatus, nameof(exchangeStatus)).ToUpperInvariant();
        IsPresentOnExchange = true;
        LastCatalogueSyncUtc = observedAtUtc;

        if (exchangeOnboardUtc is not null)
        {
            ExchangeOnboardUtc = exchangeOnboardUtc;
        }

        _permissions.Clear();
        foreach (var permission in permissions)
        {
            if (!string.IsNullOrWhiteSpace(permission))
            {
                _permissions.Add(permission.Trim().ToUpperInvariant());
            }
        }
    }

    /// <summary>
    /// Records that a catalogue synchronisation completed without observing
    /// this instrument. The instrument is not deleted and its records are
    /// preserved; it simply cannot be used.
    /// </summary>
    public void MarkAbsentFromCatalogue(DateTimeOffset observedAtUtc, string reason)
    {
        if (State == InstrumentState.Removed)
        {
            return;
        }

        IsPresentOnExchange = false;
        LastCatalogueSyncUtc = observedAtUtc;
        _permissions.Clear();
        Suspend(reason, observedAtUtc);
    }

    public void MarkFiltersLoaded(DateTimeOffset loadedAtUtc) => FiltersLoadedAtUtc = loadedAtUtc;

    /// <summary>
    /// Clears the loaded exchange filters. Filters are a hard eligibility
    /// gate, so losing them must not leave a stale timestamp behind.
    /// </summary>
    public void ClearFilters() => FiltersLoadedAtUtc = null;

    public void RecordFirstObservedCandle(DateTimeOffset openTimeUtc)
    {
        if (FirstObservedCandleUtc is null || openTimeUtc < FirstObservedCandleUtc)
        {
            FirstObservedCandleUtc = openTimeUtc;
        }
    }

    /// <summary>
    /// Listing age measured from the earliest evidence available, preferring
    /// the exchange onboarding time when the exchange supplies it.
    /// </summary>
    public TimeSpan? ListingAge(DateTimeOffset nowUtc)
    {
        var start = ExchangeOnboardUtc ?? FirstObservedCandleUtc;
        if (start is null)
        {
            return null;
        }

        var age = nowUtc - start.Value;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }

    /// <summary>
    /// Suspends the instrument. New entries and position increases are
    /// blocked; validated reduction remains permitted, and existing order and
    /// position records are preserved.
    /// </summary>
    public void Suspend(string reason, DateTimeOffset occurredAtUtc)
    {
        if (State == InstrumentState.Removed)
        {
            throw new InvalidOperationException("A removed instrument cannot be suspended.");
        }

        SuspensionReason = Require(reason, nameof(reason));
        State = InstrumentState.Suspended;
        LastSuspendedAtUtc = occurredAtUtc;
    }

    public DateTimeOffset? LastSuspendedAtUtc { get; private set; }

    /// <summary>
    /// Lifts a suspension back to <see cref="InstrumentState.Tracked"/> only.
    /// Eligibility is never restored by lifting a suspension: every grant must
    /// be re-earned from fresh evidence.
    /// </summary>
    public void LiftSuspension()
    {
        if (State != InstrumentState.Suspended)
        {
            throw new InvalidOperationException("Only a suspended instrument can have its suspension lifted.");
        }

        SuspensionReason = null;
        State = InstrumentState.Tracked;
    }

    /// <summary>
    /// Permanently removes the instrument, for example after delisting.
    /// Records are preserved; the instrument can never be used again.
    /// </summary>
    public void Remove(string reason, DateTimeOffset occurredAtUtc)
    {
        SuspensionReason = Require(reason, nameof(reason));
        State = InstrumentState.Removed;
        IsPresentOnExchange = false;
        LastSuspendedAtUtc = occurredAtUtc;
        _permissions.Clear();
    }

    /// <summary>
    /// Reclassifies the base asset, for example once an unknown asset has been
    /// reviewed. Reclassifying an instrument that is no longer a
    /// cryptocurrency suspends it, because it has left the research universe.
    /// </summary>
    public void Reclassify(AssetClass assetClass, DateTimeOffset occurredAtUtc)
    {
        AssetClass = assetClass;

        if (assetClass != AssetClass.Cryptocurrency && State != InstrumentState.Removed)
        {
            Suspend($"Asset reclassified as {assetClass} and is excluded from the research universe.", occurredAtUtc);
        }
    }

    /// <summary>
    /// Evaluates the permanent exclusions and the basic catalogue gates.
    /// Returns <see cref="InstrumentExclusionReason.None"/> only when none of
    /// them bars the instrument. This is not a grant of eligibility: the
    /// liquidity, spread, slippage, history, and data-health gates are
    /// evaluated separately.
    /// </summary>
    public InstrumentExclusionReason EvaluateExclusion(IReadOnlyCollection<string> allowedQuoteAssets)
    {
        ArgumentNullException.ThrowIfNull(allowedQuoteAssets);

        if (State == InstrumentState.Removed)
        {
            return InstrumentExclusionReason.Removed;
        }

        var classExclusion = AssetClass switch
        {
            AssetClass.Unknown => InstrumentExclusionReason.NotClassified,
            AssetClass.Stablecoin => InstrumentExclusionReason.StablecoinPair,
            AssetClass.TokenizedEquity => InstrumentExclusionReason.TokenizedEquity,
            AssetClass.Fiat => InstrumentExclusionReason.Fiat,
            AssetClass.LeveragedToken => InstrumentExclusionReason.LeveragedToken,
            _ => InstrumentExclusionReason.None
        };

        if (classExclusion != InstrumentExclusionReason.None)
        {
            return classExclusion;
        }

        if (!allowedQuoteAssets.Contains(QuoteAsset, StringComparer.OrdinalIgnoreCase))
        {
            return InstrumentExclusionReason.QuoteAssetNotAllowed;
        }

        if (!IsPresentOnExchange)
        {
            return InstrumentExclusionReason.NotPresentOnExchange;
        }

        if (!IsTradingOnExchange)
        {
            return InstrumentExclusionReason.ExchangeStatusNotTrading;
        }

        return HasSpotPermission
            ? InstrumentExclusionReason.None
            : InstrumentExclusionReason.SpotPermissionMissing;
    }

    /// <summary>
    /// True when the instrument is barred from any use beyond being tracked.
    /// </summary>
    public bool IsExcluded(IReadOnlyCollection<string> allowedQuoteAssets) =>
        EvaluateExclusion(allowedQuoteAssets) != InstrumentExclusionReason.None;

    /// <summary>
    /// New entries and position increases are blocked whenever the instrument
    /// is suspended or removed. Reduction is handled separately and remains
    /// permitted so exposure can always be closed.
    /// </summary>
    public bool BlocksNewExposure =>
        State is InstrumentState.Suspended or InstrumentState.Removed || !IsTradingOnExchange;

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        return value.Trim();
    }
}
