using Trading.MarketData;

namespace Trading.Backtesting;

/// <summary>
/// Immutable historical candle payloads, addressed by the platform manifest's version identity.
/// A manifest must never be published before its verified payload is durable.
/// </summary>
public interface IHistoricalCandlePayloadStore
{
    Task<HistoricalDatasetWriteResult> StoreAsync(
        HistoricalDataset dataset,
        IReadOnlyList<Candle> candles,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Candle>> ReadAsync(
        HistoricalDataset dataset,
        CancellationToken cancellationToken = default);
}
