namespace Trading.Exchanges.Abstractions.Account;

/// <summary>Reads a connected account's balances without moving funds.</summary>
/// <remarks>
/// Implementations must expose no transfer, deposit, withdrawal, or order
/// operation through this interface.
/// </remarks>
public interface IExchangeBalanceGateway
{
    ExchangeKind Exchange { get; }

    Task<ExchangeBalanceSnapshot> ReadBalancesAsync(
        ExchangeCredential credential,
        CancellationToken cancellationToken = default);
}

/// <summary>A read-only account balance returned by an exchange.</summary>
public sealed record ExchangeBalance(string Asset, decimal Total, decimal Available = 0m)
{
    public decimal Held => Total - Available;
}

/// <summary>A point-in-time balance reading; it is never a cached valuation.</summary>
public sealed record ExchangeBalanceSnapshot(
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyCollection<ExchangeBalance> Balances);

/// <summary>
/// Raised when an exchange cannot safely provide a current balance reading.
/// Messages must never contain credential material.
/// </summary>
public sealed class ExchangeBalanceReadException : Exception
{
    public ExchangeBalanceReadException()
    {
    }

    public ExchangeBalanceReadException(string message)
        : base(message)
    {
    }

    public ExchangeBalanceReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
