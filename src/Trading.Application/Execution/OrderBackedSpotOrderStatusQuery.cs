using Trading.Domain.Orders;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Application.Execution;

/// <summary>
/// Answers "what happened to this order?" by finding the order's own owner and
/// asking the exchange with that owner's credential.
/// </summary>
/// <remarks>
/// <para>
/// Reconciliation runs outside any request, so there is no signed-in user to
/// take the account from. The client order id is therefore resolved back to the
/// order that carries it, and the credential used is the one belonging to that
/// order's user. No other user's account is ever consulted, so a reconciliation
/// query cannot become a way to read across accounts.
/// </para>
/// <para>
/// Every failure to reach an answer is reported as
/// <see cref="OrderStatusQueryOutcome.Unavailable"/>. Only the exchange itself
/// may say an order is absent: a missing order row, a missing account or a
/// missing credential means the platform does not know, and a resubmission
/// decision must never be made on that basis.
/// </para>
/// </remarks>
public sealed class OrderBackedSpotOrderStatusQuery : IExchangeOrderStatusQuery
{
    private readonly ISpotOrderGateway _gateway;
    private readonly IOrderRepository _orders;
    private readonly IExchangeAccountRepository _accounts;
    private readonly ISpotExecutionAccountSource _credentials;

    public OrderBackedSpotOrderStatusQuery(
        ISpotOrderGateway gateway,
        IOrderRepository orders,
        IExchangeAccountRepository accounts,
        ISpotExecutionAccountSource credentials)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task<OrderStatusQueryResult> QueryByClientOrderIdAsync(
        string clientOrderId,
        string symbol,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            return OrderStatusQueryResult.Unavailable("A client order id is required.");
        }

        var order = await _orders.FindByClientOrderIdAsync(clientOrderId, cancellationToken)
            .ConfigureAwait(false);

        if (order is null)
        {
            return OrderStatusQueryResult.Unavailable(
                "No local order carries this client order id, so its owner is unknown.");
        }

        if (order.ExchangeAccountId is not { } accountId)
        {
            return OrderStatusQueryResult.Unavailable(
                "The local live order does not name the exchange account that placed it.");
        }

        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (account is null || account.UserId != order.UserId || account.ExchangeKind != _gateway.Exchange)
        {
            return OrderStatusQueryResult.Unavailable(
                "The owning exchange account could not be resolved for this order.");
        }

        var resolved = await _credentials.ResolveAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        if (resolved is null)
        {
            return OrderStatusQueryResult.Unavailable(
                "The credential for the owning account could not be resolved.");
        }

        return await _gateway
            .QueryAsync(resolved.Credential, clientOrderId, cancellationToken)
            .ConfigureAwait(false);
    }
}
