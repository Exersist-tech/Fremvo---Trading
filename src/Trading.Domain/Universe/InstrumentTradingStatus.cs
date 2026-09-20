namespace Trading.Domain.Universe;

/// <summary>
/// Exchange-neutral trading status for an instrument.
/// </summary>
/// <remarks>
/// <para>
/// Exchanges describe tradability with their own vocabulary. Comparing the
/// core domain against one exchange's literal status string would silently
/// exclude every instrument on any other exchange, which is a failure mode
/// that looks exactly like an empty universe rather than like a bug.
/// </para>
/// <para>
/// Connectors translate their own vocabulary into these values. Anything a
/// connector does not recognise becomes <see cref="Unknown"/>, which is
/// treated as not tradable, so an unrecognised status can never be mistaken
/// for permission to trade.
/// </para>
/// </remarks>
public enum InstrumentTradingStatus
{
    /// <summary>
    /// The status is unknown or was not recognised. Never tradable.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Fully tradable.
    /// </summary>
    Trading = 1,

    /// <summary>
    /// Only limit orders are accepted. Not treated as fully tradable, because
    /// a market order would be rejected.
    /// </summary>
    LimitOnly = 2,

    /// <summary>
    /// Only maker orders are accepted.
    /// </summary>
    PostOnly = 3,

    /// <summary>
    /// Only exposure-reducing orders are accepted.
    /// </summary>
    ReduceOnly = 4,

    /// <summary>
    /// Only cancellations are accepted. No new order will be filled.
    /// </summary>
    CancelOnly = 5,

    /// <summary>
    /// Trading is suspended, for example during maintenance.
    /// </summary>
    Halted = 6,

    /// <summary>
    /// The instrument has been delisted.
    /// </summary>
    Delisted = 7
}

/// <summary>
/// Neutral capability names an exchange catalogue may grant an instrument.
/// </summary>
/// <remarks>
/// Spot, margin and futures are separate capabilities, never flags on one
/// another. A connector emits only the capabilities it can positively
/// confirm.
/// </remarks>
public static class InstrumentCapabilities
{
    public const string Spot = "SPOT";

    public const string Margin = "MARGIN";

    public const string Futures = "FUTURES";
}
