using Trading.Exchanges.Abstractions;
using Trading.Infrastructure.Secrets;

namespace Trading.Application.Execution;

/// <summary>
/// Resolves an execution credential from the stored account and the secret
/// store.
/// </summary>
/// <remarks>
/// <para>
/// The secret never leaves this path. It is read at the moment an order is
/// sent, handed to the connector, and dropped. Nothing caches it, nothing logs
/// it, and no type that reaches the browser carries it.
/// </para>
/// <para>
/// An account that cannot currently reach the exchange resolves to
/// <see langword="null"/>, so a disconnected, suspended or paper-stage account
/// produces a refusal at the adapter rather than an attempt with a credential
/// that should no longer be used.
/// </para>
/// </remarks>
public sealed class ExchangeAccountExecutionSource : ISpotExecutionAccountSource
{
    private readonly IExchangeAccountRepository _accounts;
    private readonly ISecretStore _secrets;

    public ExchangeAccountExecutionSource(IExchangeAccountRepository accounts, ISecretStore secrets)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public async Task<SpotExecutionAccount?> ResolveAsync(
        Guid exchangeAccountId,
        CancellationToken cancellationToken)
    {
        var account = await _accounts.GetByIdAsync(exchangeAccountId, cancellationToken).ConfigureAwait(false);

        // Re-checked here rather than trusted from the caller. This is the last
        // place the stored account is read before its key is used, so it is the
        // last place the account's own state can still stop the order.
        if (account is null || !account.CanReachExchange)
        {
            return null;
        }

        string secret;
        string key;
        try
        {
            secret = await _secrets.GetSecretAsync(account.CredentialReference, cancellationToken)
                .ConfigureAwait(false);
            key = await _secrets.GetSecretAsync(account.CredentialReference + "/key", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // The row outlived its secret. Refusing is the only safe reading:
            // there is no credential to send and nothing to guess.
            return null;
        }

        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return new SpotExecutionAccount(
            account.Id,
            account.UserId,
            account.ExchangeKind,
            account.Stage,
            new ExchangeCredential(key, secret));
    }
}
