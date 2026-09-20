using Trading.Exchanges.Abstractions;

namespace Trading.Application.Execution;

/// <summary>
/// Everything an execution adapter needs to send one order for one account.
/// </summary>
/// <remarks>
/// The credential is held only for the duration of a single execution. It is
/// never persisted, logged, cached or returned to a caller.
/// </remarks>
public sealed class SpotExecutionAccount
{
    public SpotExecutionAccount(
        Guid accountId,
        Guid userId,
        ExchangeKind exchange,
        TradingStage stage,
        ExchangeCredential credential)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Account id is required.", nameof(accountId));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        AccountId = accountId;
        UserId = userId;
        Exchange = exchange;
        Stage = stage;
        Credential = credential ?? throw new ArgumentNullException(nameof(credential));
    }

    public Guid AccountId { get; }

    public Guid UserId { get; }

    public ExchangeKind Exchange { get; }

    public TradingStage Stage { get; }

    public ExchangeCredential Credential { get; }
}

/// <summary>
/// Resolves the account and credential an execution command will be sent with.
/// </summary>
/// <remarks>
/// Implementations must return <see langword="null"/> rather than throw when
/// the account does not exist or cannot be used, so that a missing account is
/// an ordinary refusal rather than a fault the caller might mistake for a
/// transport failure.
/// </remarks>
public interface ISpotExecutionAccountSource
{
    Task<SpotExecutionAccount?> ResolveAsync(Guid exchangeAccountId, CancellationToken cancellationToken);
}
