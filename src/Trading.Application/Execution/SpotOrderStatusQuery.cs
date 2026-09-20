using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Application.Execution;

/// <summary>
/// Asks a spot venue what actually happened to one account's order.
/// </summary>
/// <remarks>
/// <para>
/// This is the other half of <see cref="SpotExecutionAdapter"/>. The adapter
/// can report that nothing was established; this is what establishes it. The
/// two together are what allow the platform to refuse to resubmit on doubt
/// instead of refusing to submit at all.
/// </para>
/// <para>
/// The credential is resolved per call rather than captured at construction,
/// so a revoked or rotated key takes effect immediately and no long-lived
/// object holds a secret.
/// </para>
/// <para>
/// Every failure path returns <c>Unavailable</c>. None returns
/// <c>NotFound</c>, because only the exchange itself can prove an order's
/// absence, and a local failure proves nothing.
/// </para>
/// </remarks>
public sealed class SpotOrderStatusQuery : IExchangeOrderStatusQuery
{
    private readonly ISpotOrderGateway _gateway;
    private readonly ISpotExecutionAccountSource _accounts;
    private readonly Guid _exchangeAccountId;

    public SpotOrderStatusQuery(
        ISpotOrderGateway gateway,
        ISpotExecutionAccountSource accounts,
        Guid exchangeAccountId)
    {
        if (exchangeAccountId == Guid.Empty)
        {
            throw new ArgumentException("Exchange account id is required.", nameof(exchangeAccountId));
        }

        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _exchangeAccountId = exchangeAccountId;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Any connector failure must degrade to 'nothing proven'. Inspecting the "
            + "exception type would risk treating some failure as evidence that an order is absent.")]
    public async Task<OrderStatusQueryResult> QueryByClientOrderIdAsync(
        string clientOrderId,
        string symbol,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        var account = await _accounts.ResolveAsync(_exchangeAccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return OrderStatusQueryResult.Unavailable(
                "The exchange account could not be resolved, so the order's state is unknown.");
        }

        if (account.Exchange != _gateway.Exchange)
        {
            return OrderStatusQueryResult.Unavailable(
                "The exchange account belongs to a different exchange than this status query.");
        }

        try
        {
            return await _gateway
                .QueryAsync(account.Credential, clientOrderId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OrderStatusQueryResult.Unavailable(
                $"The exchange status query threw {exception.GetType().Name}.");
        }
    }
}
