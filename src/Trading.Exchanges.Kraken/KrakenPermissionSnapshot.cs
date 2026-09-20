namespace Trading.Exchanges.Kraken;

/// <summary>
/// The permissions observed on a Kraken API key.
/// </summary>
/// <remarks>
/// A key that can withdraw is never usable, even though the platform has no
/// withdrawal code. Holding a withdrawal-capable key would give the platform
/// a capability it must never have, so the key is rejected at validation.
/// </remarks>
public sealed record KrakenPermissionSnapshot(
    bool CanRead,
    bool CanTrade,
    bool CanWithdraw,
    DateTimeOffset ValidatedAtUtc)
{
    public bool IsUsableForTrading => CanRead && CanTrade && !CanWithdraw;
}
