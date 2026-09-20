using Trading.Exchanges.Abstractions;

namespace Trading.Application.Execution;

/// <summary>
/// The server-resolved identity used only by the futures demo route.
/// </summary>
public sealed class FuturesDemoExecutionAccount
{
    public FuturesDemoExecutionAccount(
        Guid accountId,
        ExchangeKind exchange,
        bool isDemoOnly,
        ExchangeCredential credential)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Futures demo account id is required.", nameof(accountId));
        }

        AccountId = accountId;
        Exchange = exchange;
        IsDemoOnly = isDemoOnly;
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
    }

    public Guid AccountId { get; }
    public ExchangeKind Exchange { get; }

    /// <summary>Must be true; a non-demo account is never accepted here.</summary>
    public bool IsDemoOnly { get; }

    public ExchangeCredential Credential { get; }
}

/// <summary>Resolves only an explicitly named futures demo account.</summary>
public interface IFuturesDemoExecutionAccountSource
{
    Task<FuturesDemoExecutionAccount?> ResolveAsync(
        Guid futuresDemoAccountId,
        CancellationToken cancellationToken);
}
