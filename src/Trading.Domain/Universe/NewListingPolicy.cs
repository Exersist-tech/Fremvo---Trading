namespace Trading.Domain.Universe;

/// <summary>
/// Restrictions applied to a recently listed instrument.
/// </summary>
/// <remarks>
/// A new listing has thin, unrepresentative history and unstable liquidity.
/// Statistics computed over that period describe a listing event, not a
/// market, so a strategy fitted to it is fitted to noise. The restriction is
/// therefore expressed in permitted purposes rather than as a warning.
/// </remarks>
public enum NewListingRestriction
{
    /// <summary>Listing age is unknown. Nothing is permitted beyond tracking.</summary>
    Unknown = 0,

    /// <summary>Too new for any use beyond collecting data.</summary>
    TrackingOnly = 1,

    /// <summary>Old enough for exploratory research and scanning only.</summary>
    ResearchOnly = 2,

    /// <summary>Old enough for backtesting and paper trading, but not live.</summary>
    SimulationOnly = 3,

    /// <summary>No listing-age restriction remains.</summary>
    Unrestricted = 4
}

/// <summary>
/// Maps listing age to the highest purpose a new listing may be used for.
/// </summary>
/// <remarks>
/// The windows are mandatory platform floors. Configuration may lengthen them
/// but never shorten them, which is enforced by <see cref="Stricter"/>.
/// </remarks>
public sealed class NewListingPolicy
{
    /// <summary>
    /// The platform floor. These are minimums, not recommendations.
    /// </summary>
    public static NewListingPolicy PlatformFloor { get; } = new(
        TimeSpan.FromDays(7),
        TimeSpan.FromDays(30),
        TimeSpan.FromDays(90));

    public NewListingPolicy(
        TimeSpan researchAfter,
        TimeSpan simulationAfter,
        TimeSpan unrestrictedAfter)
    {
        if (researchAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(researchAfter), "A positive research window is required.");
        }

        if (simulationAfter < researchAfter || unrestrictedAfter < simulationAfter)
        {
            throw new ArgumentException(
                "Windows must not decrease: research, then simulation, then unrestricted.",
                nameof(simulationAfter));
        }

        ResearchAfter = researchAfter;
        SimulationAfter = simulationAfter;
        UnrestrictedAfter = unrestrictedAfter;
    }

    public TimeSpan ResearchAfter { get; }

    public TimeSpan SimulationAfter { get; }

    public TimeSpan UnrestrictedAfter { get; }

    /// <summary>
    /// Combines this policy with a floor, taking the longer window for every
    /// stage. Configuration can therefore only delay access, never hasten it.
    /// </summary>
    public NewListingPolicy Stricter(NewListingPolicy floor)
    {
        ArgumentNullException.ThrowIfNull(floor);

        return new NewListingPolicy(
            Max(ResearchAfter, floor.ResearchAfter),
            Max(SimulationAfter, floor.SimulationAfter),
            Max(UnrestrictedAfter, floor.UnrestrictedAfter));
    }

    /// <summary>
    /// Classifies an instrument by listing age. An unknown age is
    /// <see cref="NewListingRestriction.Unknown"/>, never treated as mature.
    /// </summary>
    public NewListingRestriction Restriction(Instrument instrument, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return Restriction(instrument.ListingAge(nowUtc));
    }

    public NewListingRestriction Restriction(TimeSpan? listingAge)
    {
        if (listingAge is null)
        {
            return NewListingRestriction.Unknown;
        }

        var age = listingAge.Value;

        if (age >= UnrestrictedAfter)
        {
            return NewListingRestriction.Unrestricted;
        }

        if (age >= SimulationAfter)
        {
            return NewListingRestriction.SimulationOnly;
        }

        return age >= ResearchAfter
            ? NewListingRestriction.ResearchOnly
            : NewListingRestriction.TrackingOnly;
    }

    /// <summary>
    /// True when the listing age permits the requested purpose.
    /// </summary>
    public bool Permits(EligibilityPurpose purpose, TimeSpan? listingAge)
    {
        var restriction = Restriction(listingAge);

        return purpose switch
        {
            EligibilityPurpose.None => false,
            EligibilityPurpose.Research =>
                restriction is NewListingRestriction.ResearchOnly
                    or NewListingRestriction.SimulationOnly
                    or NewListingRestriction.Unrestricted,
            EligibilityPurpose.Backtest or EligibilityPurpose.Paper =>
                restriction is NewListingRestriction.SimulationOnly
                    or NewListingRestriction.Unrestricted,
            _ => restriction is NewListingRestriction.Unrestricted
        };
    }

    public bool Permits(EligibilityPurpose purpose, Instrument instrument, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        return Permits(purpose, instrument.ListingAge(nowUtc));
    }

    /// <summary>
    /// A human-readable reason, for the administrator view and audit records.
    /// </summary>
    public string Explain(EligibilityPurpose purpose, TimeSpan? listingAge)
    {
        var restriction = Restriction(listingAge);

        if (restriction == NewListingRestriction.Unknown)
        {
            return $"{purpose} blocked: listing age is unknown.";
        }

        return Permits(purpose, listingAge)
            ? $"{purpose} permitted: listed for {listingAge!.Value.TotalDays:F1} days ({restriction})."
            : $"{purpose} blocked: listed for {listingAge!.Value.TotalDays:F1} days, which allows {restriction} only.";
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
}
