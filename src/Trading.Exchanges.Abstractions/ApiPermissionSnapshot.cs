namespace Trading.Exchanges.Abstractions;

public sealed record ApiPermissionSnapshot(
    bool CanRead,
    bool CanTrade,
    bool CanWithdraw,
    DateTimeOffset ValidatedAtUtc)
{
    public bool AllowsTrading => CanRead && CanTrade && !CanWithdraw;

    public bool IsStale(TimeSpan maxAge, DateTimeOffset nowUtc)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);

        return nowUtc - ValidatedAtUtc > maxAge;
    }
}
