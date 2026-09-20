using System.Globalization;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Application.Execution;

/// <summary>
/// Brings the platform's record of live orders into line with the exchange.
/// </summary>
/// <remarks>
/// <para>
/// A live order is accepted long before it is filled, and it may be filled,
/// partly filled, cancelled or expired without the platform being told. This
/// service asks. Until it does, the platform's view of a working order is a
/// hope rather than a fact.
/// </para>
/// <para>
/// It is the only place a position is created from live trading, and it does
/// so strictly from quantities the exchange reported. Nothing here infers a
/// fill from the passage of time or from the order having been accepted.
/// </para>
/// <para>
/// It never places, amends or cancels an order. It only reads and records.
/// </para>
/// </remarks>
public sealed class LiveOrderSyncService
{
    private readonly IOrderRepository _orders;
    private readonly IPositionRepository _positions;
    private readonly IExchangeAccountRepository _accounts;
    private readonly ISpotOrderGateway _gateway;
    private readonly ISpotExecutionAccountSource _credentials;
    private readonly IAuditEventWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public LiveOrderSyncService(
        IOrderRepository orders,
        IPositionRepository positions,
        IExchangeAccountRepository accounts,
        ISpotOrderGateway gateway,
        ISpotExecutionAccountSource credentials,
        IAuditEventWriter auditWriter,
        TimeProvider timeProvider)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Refreshes every working live order this user has, and applies any fills
    /// the exchange reports.
    /// </summary>
    /// <returns>The number of orders whose recorded state changed.</returns>
    public async Task<int> SyncAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        var orders = await _orders.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        var working = orders
            .Where(order => order.Mode == TradingMode.Live && IsWorking(order.State))
            .ToList();

        var changed = 0;

        foreach (var order in working)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A live order is bound to the account that placed it. Choosing an
            // arbitrary connected account here would leak across a user's own
            // accounts and can return "not found" for an order that is live in
            // the other account.
            if (order.ExchangeAccountId is not { } accountId)
            {
                continue;
            }

            var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (account is null || account.UserId != userId || account.ExchangeKind != _gateway.Exchange)
            {
                continue;
            }

            var resolved = await _credentials.ResolveAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                continue;
            }

            var status = await _gateway
                .QueryAsync(resolved.Credential, order.ClientOrderId, cancellationToken)
                .ConfigureAwait(false);

            // An unavailable answer changes nothing. Failing to learn an
            // order's state is not evidence about its state, and writing a
            // guess here would corrupt the position record.
            if (status.Outcome != OrderStatusQueryOutcome.Found)
            {
                continue;
            }

            if (await ApplyAsync(order, status, cancellationToken).ConfigureAwait(false))
            {
                changed++;
            }
        }

        return changed;
    }

    private static bool IsWorking(OrderState state) =>
        state is OrderState.New or OrderState.Accepted or OrderState.PartiallyFilled;

    private async Task<bool> ApplyAsync(
        Order order,
        OrderStatusQueryResult status,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var newlyFilled = status.FilledQuantity - order.FilledQuantity;

        // The exchange's cumulative figure is authoritative, but it must never
        // move backwards. A smaller number than the platform already recorded
        // means the answer is inconsistent, not that a fill was undone.
        if (newlyFilled < 0m)
        {
            return false;
        }

        var changed = false;

        if (newlyFilled > 0m)
        {
            order.MarkPartiallyFilled(status.FilledQuantity, now);
            await ApplyFillToPositionAsync(order, newlyFilled, cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        switch (status.State)
        {
            case ExchangeOrderState.Filled when order.State != OrderState.Filled:
                order.MarkFilled();
                changed = true;
                break;

            case ExchangeOrderState.Canceled when order.State != OrderState.Canceled:
                order.MarkCanceled();
                changed = true;
                break;

            case ExchangeOrderState.Expired when order.State != OrderState.Canceled:
                order.MarkCanceled();
                changed = true;
                break;

            case ExchangeOrderState.Rejected when order.State != OrderState.Rejected:
                order.MarkRejected("The exchange reported this order as rejected.");
                changed = true;
                break;

            default:
                break;
        }

        if (!changed)
        {
            return false;
        }

        await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            new AuditEvent(
                Guid.NewGuid(),
                order.UserId,
                "LiveOrderSynced",
                targetType: "LiveOrder",
                targetId: order.Id.ToString("D", CultureInfo.InvariantCulture),
                occurredAtUtc: now,
                before: null,
                after: string.Create(
                    CultureInfo.InvariantCulture,
                    $"The exchange reported {status.State} with {status.FilledQuantity} filled."),
                correlationId: order.ClientOrderId),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Applies an observed fill to the user's position for the instrument.
    /// </summary>
    /// <remarks>
    /// A buy that meets an open short, or a sell that meets an open long,
    /// reduces it. Anything that would carry the position through zero into the
    /// opposite direction is left unapplied rather than guessed at, because the
    /// entry price of the resulting position cannot be derived from a single
    /// reduction.
    /// </remarks>
    private async Task ApplyFillToPositionAsync(
        Order order,
        decimal filledQuantity,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var open = await _positions.ListOpenAsync(order.UserId, cancellationToken).ConfigureAwait(false);
        var existing = open.FirstOrDefault(position =>
            string.Equals(position.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase)
            && position.Status == PositionStatus.Open
            && position.Mode == TradingMode.Live);

        if (existing is null)
        {
            var position = new Position(
                Guid.NewGuid(),
                order.UserId,
                order.StrategyId,
                order.Symbol,
                order.Side == OrderSide.Buy ? PositionDirection.DirectionLong : PositionDirection.DirectionShort,
                filledQuantity,
                order.Price,
                order.Price,
                now,
                mode: TradingMode.Live);

            await _positions.AddAsync(position, cancellationToken).ConfigureAwait(false);
            return;
        }

        var reduces =
            (existing.Direction == PositionDirection.DirectionLong && order.Side == OrderSide.Sell)
            || (existing.Direction == PositionDirection.DirectionShort && order.Side == OrderSide.Buy);

        if (!reduces || filledQuantity > existing.Quantity)
        {
            // Increasing a position would need an averaged cost basis the
            // position model does not carry, and reducing past zero would flip
            // its direction. Inventing either would misstate every profit and
            // loss figure derived from the entry price.
            existing.UpdateMarkPrice(order.Price);
            await _positions.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
            return;
        }

        existing.Reduce(filledQuantity);
        existing.UpdateMarkPrice(order.Price);
        await _positions.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
    }
}
