namespace Trading.Exchanges.Kraken.MarketData;

internal static class KrakenV2SymbolNames
{
    public static string ForPublicStream(string symbol)
    {
        var slash = symbol.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
            return symbol;

        var asset = symbol[..slash];
        var normalized = asset.ToUpperInvariant() switch
        {
            "XBT" => "BTC",
            "XDG" => "DOGE",
            _ => asset
        };
        return normalized + symbol[slash..];
    }
}
