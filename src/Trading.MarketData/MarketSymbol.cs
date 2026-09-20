namespace Trading.MarketData;

public sealed class MarketSymbol
{
    public MarketSymbol(
        string symbol,
        string baseAsset,
        string quoteAsset,
        bool isActive,
        string exchangeName)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            throw new ArgumentException("Base asset is required.", nameof(baseAsset));
        }

        if (string.IsNullOrWhiteSpace(quoteAsset))
        {
            throw new ArgumentException("Quote asset is required.", nameof(quoteAsset));
        }

        if (string.IsNullOrWhiteSpace(exchangeName))
        {
            throw new ArgumentException("Exchange name is required.", nameof(exchangeName));
        }

        Symbol = symbol.Trim();
        BaseAsset = baseAsset.Trim();
        QuoteAsset = quoteAsset.Trim();
        IsActive = isActive;
        ExchangeName = exchangeName.Trim();
    }

    public string Symbol { get; }

    public string BaseAsset { get; }

    public string QuoteAsset { get; }

    public bool IsActive { get; }

    public string ExchangeName { get; }
}
