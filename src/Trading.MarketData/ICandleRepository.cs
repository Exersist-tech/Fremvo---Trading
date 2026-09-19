namespace Trading.MarketData;

public interface ICandleRepository
{
    Task<Candle?> GetLatestAsync(string symbol, Trading.Domain.Market.CandleInterval interval, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Candle>> ListAsync(
        string symbol,
        Trading.Domain.Market.CandleInterval interval,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}
