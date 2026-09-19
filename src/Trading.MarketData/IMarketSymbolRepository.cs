namespace Trading.MarketData;

public interface IMarketSymbolRepository
{
    Task<MarketSymbol?> GetAsync(string symbol, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<MarketSymbol>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default);
}
