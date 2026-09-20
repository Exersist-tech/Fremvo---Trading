using Microsoft.EntityFrameworkCore;
using Trading.Application.Execution;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;

namespace Trading.Infrastructure.Data.Execution;

/// <summary>
/// Entity Framework backed order storage.
/// </summary>
/// <remarks>
/// Reads are always filtered by user id inside this class. Callers cannot
/// opt out of isolation.
/// </remarks>
public sealed class EfOrderRepository : IOrderRepository
{
    private readonly TradingDbContext _context;

    public EfOrderRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<Order?> GetAsync(Guid userId, Guid orderId, CancellationToken cancellationToken) =>
        await _context.Orders
            .AsNoTracking()
            .SingleOrDefaultAsync(order => order.Id == orderId && order.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<Order?> FindByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);

        var trimmed = clientOrderId.Trim();

        return await _context.Orders
            .AsNoTracking()
            .SingleOrDefaultAsync(order => order.ClientOrderId == trimmed, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Order?> FindByOrderIdForReconciliationAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        await _context.Orders
            .SingleOrDefaultAsync(order => order.Id == orderId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Order>> ListAsync(Guid userId, CancellationToken cancellationToken) =>        await _context.Orders
            .AsNoTracking()
            .Where(order => order.UserId == userId)
            .OrderByDescending(order => order.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Order>> ListAwaitingReconciliationAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await _context.Orders
            .AsNoTracking()
            .Where(order => order.UserId == userId && order.RequiresReconciliation)
            .OrderBy(order => order.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Checked before insert so the common case produces a clear domain
        // failure, and enforced again by the unique index so a race between
        // two instances cannot create a duplicate live order.
        var existing = await _context.Orders
            .AsNoTracking()
            .AnyAsync(candidate => candidate.ClientOrderId == order.ClientOrderId, cancellationToken)
            .ConfigureAwait(false);

        if (existing)
        {
            throw new DuplicateClientOrderIdException(order.ClientOrderId);
        }

        _context.Orders.Add(order);

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            _context.Entry(order).State = EntityState.Detached;
            throw new DuplicateClientOrderIdException(order.ClientOrderId);
        }
    }

    public async Task UpdateAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);

        _context.Orders.Update(order);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true ||
        exception.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
}

public sealed class EfPositionRepository : IPositionRepository
{
    private readonly TradingDbContext _context;

    public EfPositionRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<Position?> GetAsync(Guid userId, Guid positionId, CancellationToken cancellationToken) =>
        await _context.Positions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                position => position.Id == positionId && position.UserId == userId,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Position>> ListOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        await _context.Positions
            .AsNoTracking()
            .Where(position => position.UserId == userId && position.Status != PositionStatus.Flat)
            .OrderBy(position => position.Symbol)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task AddAsync(Position position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);

        _context.Positions.Add(position);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Position position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);

        _context.Positions.Update(position);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class EfOrderReconciliationRepository : IOrderReconciliationRepository
{
    private readonly TradingDbContext _context;

    public EfOrderReconciliationRepository(TradingDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<OrderReconciliationRecord?> GetAsync(Guid recordId, CancellationToken cancellationToken) =>
        await _context.OrderReconciliations
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == recordId, cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<OrderReconciliationRecord>> ListUnresolvedAsync(
        CancellationToken cancellationToken) =>
        await _context.OrderReconciliations
            .AsNoTracking()
            .Where(record => record.ResolvedAtUtc == null)
            .OrderBy(record => record.ObservedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<OrderReconciliationRecord>> ListForOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        await _context.OrderReconciliations
            .AsNoTracking()
            .Where(record => record.OrderId == orderId)
            .OrderBy(record => record.ObservedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task AddAsync(OrderReconciliationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        _context.OrderReconciliations.Add(record);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(OrderReconciliationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        _context.OrderReconciliations.Update(record);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
