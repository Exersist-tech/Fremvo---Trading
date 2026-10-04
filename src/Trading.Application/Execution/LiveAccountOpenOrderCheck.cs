using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Application.Execution;

public interface ILiveAccountOpenOrderCheck
{
    Task<SpotOpenOrderState> ReadAsync(
        Guid userId, Guid accountId, ExchangeKind exchange, CancellationToken cancellationToken);
}

/// <summary>Resolves only the selected owner's credential for a read-only venue check.</summary>
public sealed class LiveAccountOpenOrderCheck : ILiveAccountOpenOrderCheck
{
    private readonly ISpotOpenOrderGateway _gateway;
    private readonly ISpotExecutionAccountSource _credentials;

    public LiveAccountOpenOrderCheck(
        ISpotOpenOrderGateway gateway,
        ISpotExecutionAccountSource credentials)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task<SpotOpenOrderState> ReadAsync(
        Guid userId, Guid accountId, ExchangeKind exchange, CancellationToken cancellationToken)
    {
        if (_gateway.Exchange != exchange)
        {
            return SpotOpenOrderState.Unavailable;
        }

        var account = await _credentials.ResolveAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null || account.UserId != userId || account.Exchange != exchange
            || account.AccountId != accountId)
        {
            return SpotOpenOrderState.Unavailable;
        }

        return await _gateway.ReadAsync(account.Credential, cancellationToken).ConfigureAwait(false);
    }
}
