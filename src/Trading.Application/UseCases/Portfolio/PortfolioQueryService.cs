using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Account;
using Trading.Infrastructure.Secrets;

namespace Trading.Application.UseCases.Portfolio;

/// <summary>Retrieves fresh, read-only exchange balances for a user's accounts.</summary>
public interface IPortfolioQueryService
{
    Task<IReadOnlyCollection<PortfolioAccountReading>> ReadAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<PortfolioAccountReading?> ReadAccountAsync(
        Guid userId, Guid accountId, CancellationToken cancellationToken = default);
}

public sealed record PortfolioAccountReading(
    Guid AccountId,
    string DisplayName,
    ExchangeKind Exchange,
    DateTimeOffset? RetrievedAtUtc,
    IReadOnlyCollection<ExchangeBalance> Balances,
    string? Error);

public sealed class PortfolioQueryService : IPortfolioQueryService
{
    private readonly IExchangeAccountRepository _accounts;
    private readonly ISecretStore _secrets;
    private readonly Dictionary<ExchangeKind, IExchangeBalanceGateway> _gateways;

    public PortfolioQueryService(
        IExchangeAccountRepository accounts,
        ISecretStore secrets,
        IEnumerable<IExchangeBalanceGateway> gateways)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        ArgumentNullException.ThrowIfNull(gateways);
        _gateways = gateways.ToDictionary(gateway => gateway.Exchange);
    }

    public async Task<IReadOnlyCollection<PortfolioAccountReading>> ReadAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var accounts = await _accounts.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false);
        var readings = new List<PortfolioAccountReading>(accounts.Count);

        foreach (var account in accounts)
        {
            readings.Add(await ReadOwnedAccountAsync(account, cancellationToken).ConfigureAwait(false));
        }

        return readings;
    }

    public async Task<PortfolioAccountReading?> ReadAccountAsync(
        Guid userId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
        return account is null || account.UserId != userId
            ? null
            : await ReadOwnedAccountAsync(account, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PortfolioAccountReading> ReadOwnedAccountAsync(
        ExchangeAccount account, CancellationToken cancellationToken)
    {
        // Ownership is checked before resolving any credential.
        if (!account.CanTrade)
            return Unavailable(account, "This account is not connected and validated.");

        if (!_gateways.TryGetValue(account.ExchangeKind, out var gateway))
            return Unavailable(account, "Balance reading is not supported for this exchange.");

        try
        {
            var secret = await _secrets.GetSecretAsync(account.CredentialReference, cancellationToken)
                .ConfigureAwait(false);
            var key = await _secrets.GetSecretAsync(account.CredentialReference + "/key", cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await gateway.ReadBalancesAsync(
                new ExchangeCredential(key, secret), cancellationToken).ConfigureAwait(false);

            return new PortfolioAccountReading(
                account.Id, account.DisplayName, account.ExchangeKind, snapshot.RetrievedAtUtc,
                snapshot.Balances, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // A connector or secret provider must not leak implementation
        // details to the browser. Every failed fresh read is represented
        // as unavailable rather than risking a stale balance.
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Never reuse an older reading: a failed refresh must be
            // visible as a failure, not presented as a current balance.
            return Unavailable(account, "The exchange could not provide a current balance reading.");
        }
    }

    private static PortfolioAccountReading Unavailable(ExchangeAccount account, string error) =>
        new(account.Id, account.DisplayName, account.ExchangeKind, null, Array.Empty<ExchangeBalance>(), error);
}
