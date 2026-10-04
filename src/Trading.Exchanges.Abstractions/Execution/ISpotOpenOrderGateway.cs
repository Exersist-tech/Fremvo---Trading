namespace Trading.Exchanges.Abstractions.Execution;

/// <summary>Whether a selected exchange account currently has any working Spot orders.</summary>
public enum SpotOpenOrderState
{
    Unavailable = 0,
    Empty = 1,
    Present = 2
}

/// <summary>
/// Reads the exchange's whole open-order book for one credential. Unavailable
/// must never be interpreted as an empty book.
/// </summary>
public interface ISpotOpenOrderGateway
{
    ExchangeKind Exchange { get; }

    Task<SpotOpenOrderState> ReadAsync(
        ExchangeCredential credential,
        CancellationToken cancellationToken = default);
}
