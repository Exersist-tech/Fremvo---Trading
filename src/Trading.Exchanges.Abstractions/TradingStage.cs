namespace Trading.Exchanges.Abstractions;

/// <summary>
/// How far an exchange account has been cleared to trade.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a property of the account rather than a display
/// option. A user interface toggle that switched between simulated and real
/// money would imply the same account can move between them freely, which is
/// how accidental real orders happen. Promotion is a one-way, audited change
/// that the platform must authorize.
/// </para>
/// <para>
/// The stages are ordered and cannot be skipped. An account proves itself
/// against simulated fills first, then against a supervised minimum-size real
/// path, and only then against unrestricted live sizing.
/// </para>
/// </remarks>
public enum TradingStage
{
    /// <summary>
    /// Simulated money only. No order ever reaches the exchange. This is the
    /// stage every account starts in.
    /// </summary>
    Paper = 0,

    /// <summary>
    /// The supervised minimum-size real path used to prove the execution and
    /// reconciliation route, because Kraken publishes no public Spot sandbox.
    /// Real funds are at risk here, bounded by a risk-engine notional ceiling.
    /// </summary>
    Proving = 1,

    /// <summary>
    /// Unrestricted live trading within the user's configured risk limits and
    /// the mandatory platform ceilings.
    /// </summary>
    Live = 2
}
