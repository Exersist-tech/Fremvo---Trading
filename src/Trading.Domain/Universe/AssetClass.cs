namespace Trading.Domain.Universe;

/// <summary>
/// Classification of the base asset of an instrument. Only
/// <see cref="Cryptocurrency"/> may ever exceed <see cref="InstrumentState.Tracked"/>;
/// every other class is permanently excluded from the research universe.
/// </summary>
public enum AssetClass
{
    /// <summary>
    /// The class has not been established. Treated as excluded: an asset we
    /// cannot classify never receives an eligibility grant.
    /// </summary>
    Unknown = 0,
    Cryptocurrency = 1,
    Stablecoin = 2,
    TokenizedEquity = 3,
    Fiat = 4,

    /// <summary>
    /// Leveraged or rebalanced basket tokens. Their value is a derived,
    /// periodically rebalanced quantity, which breaks the candle-based
    /// assumptions every strategy template relies on.
    /// </summary>
    LeveragedToken = 5
}
