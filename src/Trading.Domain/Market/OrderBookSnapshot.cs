namespace Trading.Domain.Market;

public sealed record OrderBookLevel(decimal Price, decimal Quantity);

public sealed record OrderBookSnapshot(
    string Symbol,
    DateTimeOffset AsOfUtc,
    IReadOnlyList<OrderBookLevel> Bids,
    IReadOnlyList<OrderBookLevel> Asks,
    uint Checksum,
    bool IsSynchronized);
