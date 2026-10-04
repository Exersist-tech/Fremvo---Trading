using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
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

        if (order.Mode == TradingMode.Live && _context.Database.CurrentTransaction is null)
        {
            var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            await using var lifetime = transaction.ConfigureAwait(false);
            await InsertAsync(order, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        await InsertAsync(order, cancellationToken).ConfigureAwait(false);
    }

    private async Task InsertAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.Mode == TradingMode.Live && order.ExchangeAccountId is { } accountId)
        {
            await ReserveLiveAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (await _context.Orders.AsNoTracking().AnyAsync(existing =>
                existing.Mode == TradingMode.Live && existing.ExchangeAccountId == accountId
                && (existing.RequiresReconciliation
                    || (existing.State != OrderState.Filled && existing.State != OrderState.Canceled
                        && existing.State != OrderState.Rejected && existing.State != OrderState.Expired)),
                cancellationToken).ConfigureAwait(false))
                throw new WorkingLiveOrderConflictException();
        }

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
        catch (DbUpdateException exception) when (IsClientIdConflict(exception))
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

    public async Task UpdateAsync(Order order, int expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        _context.Orders.Update(order);
        _context.Entry(order).Property(value => value.Version).OriginalValue = expectedVersion;
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReserveLiveAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var resource = new SqlParameter("@resource", SqlDbType.NVarChar, 255)
        {
            Value = $"Trading.LiveOrder:{accountId:D}"
        };
        await _context.Database.ExecuteSqlRawAsync(
            "EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', "
            + "@LockOwner = 'Transaction', @LockTimeout = 20000;",
            [result, resource], cancellationToken).ConfigureAwait(false);
        if ((int)result.Value == -1)
            throw new WorkingLiveOrderConflictException();
        if ((int)result.Value < 0)
            throw new InvalidOperationException("A live exchange-account reservation could not be acquired.");
    }

    private static bool IsClientIdConflict(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("IX_Orders_ClientOrderId", StringComparison.OrdinalIgnoreCase);
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

    public async Task UpdateAsync(Position position, int expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);

        _context.Positions.Update(position);
        _context.Entry(position).Property(value => value.Version).OriginalValue = expectedVersion;
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
