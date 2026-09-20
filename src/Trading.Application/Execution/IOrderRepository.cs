using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;

namespace Trading.Application.Execution;

/// <summary>
/// Durable storage for orders.
/// </summary>
/// <remarks>
/// Every read is scoped by user id. Isolation is enforced at the repository
/// boundary rather than left to callers, because a single forgotten filter
/// would expose one user's trading activity to another.
/// </remarks>
public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);

    Task<Order?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads an order without a user filter, for the reconciliation worker.
    /// </summary>
    /// <remarks>
    /// This is the one unscoped read in the repository. It is reachable only
    /// from the reconciliation path, which acts on behalf of the platform
    /// rather than a signed-in user and never returns the order to a browser.
    /// </remarks>
    Task<Order?> FindByOrderIdForReconciliationAsync(Guid orderId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Order>> ListAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Orders frozen awaiting reconciliation. These block resubmission, so
    /// they must be visible to operators.
    /// </summary>
    Task<IReadOnlyList<Order>> ListAwaitingReconciliationAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new order.
    /// </summary>
    /// <exception cref="DuplicateClientOrderIdException">
    /// An order with the same client order id already exists. This is the
    /// durable half of duplicate-order protection: the in-memory guard is
    /// lost on restart, a unique index is not.
    /// </exception>
    Task AddAsync(Order order, CancellationToken cancellationToken);

    Task UpdateAsync(Order order, CancellationToken cancellationToken);
}

/// <summary>
/// Raised when a client order id is reused. Treated as a duplicate-order
/// attempt, never as a retryable error.
/// </summary>
public sealed class DuplicateClientOrderIdException : Exception
{
    public DuplicateClientOrderIdException(string clientOrderId)
        : base($"An order already exists with client order id '{clientOrderId}'.") =>
        ClientOrderId = clientOrderId;

    public DuplicateClientOrderIdException()
        : base("An order already exists with this client order id.") => ClientOrderId = string.Empty;

    public DuplicateClientOrderIdException(string message, Exception innerException)
        : base(message, innerException) => ClientOrderId = string.Empty;

    public string ClientOrderId { get; }
}

public interface IPositionRepository
{
    Task<Position?> GetAsync(Guid userId, Guid positionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Position>> ListOpenAsync(Guid userId, CancellationToken cancellationToken);

    Task AddAsync(Position position, CancellationToken cancellationToken);

    Task UpdateAsync(Position position, CancellationToken cancellationToken);
}

public interface IOrderReconciliationRepository
{
    Task<OrderReconciliationRecord?> GetAsync(Guid recordId, CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderReconciliationRecord>> ListUnresolvedAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<OrderReconciliationRecord>> ListForOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken);

    Task AddAsync(OrderReconciliationRecord record, CancellationToken cancellationToken);

    Task UpdateAsync(OrderReconciliationRecord record, CancellationToken cancellationToken);
}
