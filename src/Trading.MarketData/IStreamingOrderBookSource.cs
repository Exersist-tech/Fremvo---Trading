using Trading.Domain.Market;

namespace Trading.MarketData;

public interface IStreamingOrderBookSource
{
    IAsyncEnumerable<OrderBookSnapshot> StreamAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}
