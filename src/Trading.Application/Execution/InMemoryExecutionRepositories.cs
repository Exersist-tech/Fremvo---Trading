using System.Collections.Concurrent;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;

namespace Trading.Application.Execution;

/// <summary>
/// Non-durable order storage used for tests, paper trading and local runs.
/// </summary>
/// <remarks>
/// This implementation must never back live trading. It loses every order on
/// restart, which would leave real exchange orders with no local record and
/// therefore no reconciliation.
/// </remarks>
public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();
    private readonly ConcurrentDictionary<string, Guid> _clientOrderIds =
        new(StringComparer.Ordinal);

    public Task<Order?> GetAsync(Guid userId, Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(
            _orders.TryGetValue(orderId, out var order) && order.UserId == userId ? order : null);

    public Task<Order?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        return Task.FromResult(
            _clientOrderIds.TryGetValue(clientOrderId.Trim(), out var id) && _orders.TryGetValue(id, out var order)
                ? order
                : null);
    }

    public Task<Order?> FindByOrderIdForReconciliationAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_orders.TryGetValue(orderId, out var order) ? order : null);

    public Task<IReadOnlyList<Order>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>(
            _orders.Values
                .Where(order => order.UserId == userId)
                .OrderByDescending(order => order.CreatedAtUtc)
                .ToList());

    public Task<IReadOnlyList<Order>> ListAwaitingReconciliationAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Order>>(
            _orders.Values
                .Where(order => order.UserId == userId && order.RequiresReconciliation)
                .OrderBy(order => order.CreatedAtUtc)
                .ToList());

    public Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!_clientOrderIds.TryAdd(order.ClientOrderId, order.Id))
        {
            throw new DuplicateClientOrderIdException(order.ClientOrderId);
        }

        _orders[order.Id] = order;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        _orders[order.Id] = order;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Non-durable position storage. Not suitable for live trading.
/// </summary>
public sealed class InMemoryPositionRepository : IPositionRepository
{
    private readonly ConcurrentDictionary<Guid, Position> _positions = new();

    public Task<Position?> GetAsync(Guid userId, Guid positionId, CancellationToken cancellationToken) =>
        Task.FromResult(
            _positions.TryGetValue(positionId, out var position) && position.UserId == userId ? position : null);

    public Task<IReadOnlyList<Position>> ListOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Position>>(
            _positions.Values
                .Where(position => position.UserId == userId && position.Status != PositionStatus.Flat)
                .OrderBy(position => position.Symbol, StringComparer.Ordinal)
                .ToList());

    public Task AddAsync(Position position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);

        _positions[position.Id] = position;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Position position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);

        _positions[position.Id] = position;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Non-durable reconciliation storage. Not suitable for live trading.
/// </summary>
public sealed class InMemoryOrderReconciliationRepository : IOrderReconciliationRepository
{
    private readonly ConcurrentDictionary<Guid, OrderReconciliationRecord> _records = new();

    public Task<OrderReconciliationRecord?> GetAsync(Guid recordId, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryGetValue(recordId, out var record) ? record : null);

    public Task<IReadOnlyList<OrderReconciliationRecord>> ListUnresolvedAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderReconciliationRecord>>(
            _records.Values
                .Where(record => !record.IsResolved)
                .OrderBy(record => record.ObservedAtUtc)
                .ToList());

    public Task<IReadOnlyList<OrderReconciliationRecord>> ListForOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OrderReconciliationRecord>>(
            _records.Values
                .Where(record => record.OrderId == orderId)
                .OrderBy(record => record.ObservedAtUtc)
                .ToList());

    public Task AddAsync(OrderReconciliationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        _records[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(OrderReconciliationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        _records[record.Id] = record;
        return Task.CompletedTask;
    }
}
