namespace Trading.Exchanges.Binance;

public sealed record BinancePermissionSnapshot(
    bool CanRead,
    bool CanTrade,
    bool CanWithdraw,
    DateTimeOffset ValidatedAtUtc)
{
    public bool IsUsableForTrading => CanRead && CanTrade && !CanWithdraw;
}