using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Exchanges.Abstractions.Execution;

namespace Trading.Application.Execution;

/// <summary>
/// What the platform is allowed to do with an order after reconciliation.
/// </summary>
public enum ReconciliationDisposition
{
    /// <summary>
    /// Nothing was proven. The order stays frozen and a human must look at it.
    /// </summary>
    Unresolved = 0,

    /// <summary>
    /// The exchange proved the order never reached the matching engine. A
    /// fresh submission is safe.
    /// </summary>
    SafeToResubmit = 1,

    /// <summary>
    /// The order is, or was, live. Its recorded state now matches the
    /// exchange and it must not be resubmitted.
    /// </summary>
    AdoptedExchangeState = 2
}

public sealed record ReconciliationOutcome(
    Guid OrderId,
    ReconciliationDisposition Disposition,
    ExchangeOrderStatus ObservedStatus,
    string Reason)
{
    /// <summary>
    /// Resubmission is permitted only by positive proof. Every other
    /// disposition blocks.
    /// </summary>
    public bool MayResubmit => Disposition == ReconciliationDisposition.SafeToResubmit;
}

/// <summary>
/// Establishes the true exchange state of orders whose outcome is unknown.
/// </summary>
/// <remarks>
/// <para>
/// This service exists so the platform never has to guess. When a submission
/// times out the order may or may not be live. Resubmitting on a guess
/// doubles real exposure, so the order is frozen until the exchange itself
/// answers the question.
/// </para>
/// <para>
/// The service never submits or cancels anything. It only reads from the
/// exchange and records what it learned.
/// </para>
/// </remarks>
public sealed class OrderReconciliationService
{
    private readonly IOrderRepository _orders;
    private readonly IOrderReconciliationRepository _records;
    private readonly IExchangeOrderStatusQuery _statusQuery;
    private readonly IAuditEventWriter _audit;
    private readonly TimeProvider _timeProvider;

    public OrderReconciliationService(
        IOrderRepository orders,
        IOrderReconciliationRepository records,
        IExchangeOrderStatusQuery statusQuery,
        IAuditEventWriter audit,
        TimeProvider timeProvider)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _statusQuery = statusQuery ?? throw new ArgumentNullException(nameof(statusQuery));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Freezes an order whose exchange outcome is unknown and opens a
    /// reconciliation record for it.
    /// </summary>
    public async Task<OrderReconciliationRecord> OpenAsync(
        Order order,
        string source,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = _timeProvider.GetUtcNow();

        order.MarkUnknown(reason, now);
        await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

        var record = new OrderReconciliationRecord(
            Guid.NewGuid(),
            order.Id,
            order.ExchangeOrderId,
            ExchangeOrderStatus.Unknown,
            now,
            source);

        await _records.AddAsync(record, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(order, "Trade.ReconciliationOpened", reason, now, cancellationToken)
            .ConfigureAwait(false);

        return record;
    }

    /// <summary>
    /// Asks the exchange what actually happened and applies the answer.
    /// </summary>
    public async Task<ReconciliationOutcome> ResolveAsync(
        OrderReconciliationRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var order = await _orders
            .FindByOrderIdForReconciliationAsync(record.OrderId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Order '{record.OrderId}' referenced by a reconciliation record no longer exists.");

        var result = await QueryAsync(order, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        if (result.Outcome == OrderStatusQueryOutcome.Unavailable)
        {
            // Deliberately does not resolve the record. Failing to learn the
            // outcome is not evidence about the outcome.
            var reason = result.FailureReason ?? "The exchange status query failed.";
            await WriteAuditAsync(order, "Trade.ReconciliationUnavailable", reason, now, cancellationToken)
                .ConfigureAwait(false);

            return new ReconciliationOutcome(
                order.Id, ReconciliationDisposition.Unresolved, ExchangeOrderStatus.Unknown, reason);
        }

        var status = MapStatus(result.State);

        if (result.Outcome == OrderStatusQueryOutcome.NotFound)
        {
            const string NotFoundReason =
                "The exchange confirmed no order exists with this client order id, so it was never accepted.";

            record.Resolve(ExchangeOrderStatus.Rejected, NotFoundReason, now);
            await _records.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

            order.ResolveReconciliation(
                OrderState.Rejected, order.FilledQuantity, order.ExchangeOrderId, NotFoundReason, now);
            await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

            await WriteAuditAsync(order, "Trade.ReconciliationResolved", NotFoundReason, now, cancellationToken)
                .ConfigureAwait(false);

            return new ReconciliationOutcome(
                order.Id, ReconciliationDisposition.SafeToResubmit, ExchangeOrderStatus.Rejected, NotFoundReason);
        }

        var foundReason =
            $"The exchange reported status {status} with {result.FilledQuantity} filled.";

        record.Resolve(status, foundReason, now);
        await _records.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

        order.ResolveReconciliation(
            MapOrderState(result.State, order),
            Math.Max(result.FilledQuantity, order.FilledQuantity),
            result.ExchangeOrderId ?? order.ExchangeOrderId,
            foundReason,
            now);
        await _orders.UpdateAsync(order, cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(order, "Trade.ReconciliationResolved", foundReason, now, cancellationToken)
            .ConfigureAwait(false);

        var disposition = record.ProvesOrderIsNotLive
            ? ReconciliationDisposition.SafeToResubmit
            : ReconciliationDisposition.AdoptedExchangeState;

        return new ReconciliationOutcome(order.Id, disposition, status, foundReason);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Any connector failure must degrade to 'nothing proven'. Narrowing the catch would let an unanticipated exception escape and be mistaken for a decision.")]
    private async Task<OrderStatusQueryResult> QueryAsync(Order order, CancellationToken cancellationToken)
    {
        try
        {
            return await _statusQuery
                .QueryByClientOrderIdAsync(order.ClientOrderId, order.Symbol, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Any connector failure degrades to "nothing proven". The
            // exception type is intentionally not inspected: no transport
            // error can be read as evidence that an order is absent.
            return OrderStatusQueryResult.Unavailable(
                $"The exchange status query threw {exception.GetType().Name}.");
        }
    }

    private static ExchangeOrderStatus MapStatus(ExchangeOrderState state) => state switch
    {
        ExchangeOrderState.New => ExchangeOrderStatus.New,
        ExchangeOrderState.PendingNew => ExchangeOrderStatus.PendingNew,
        ExchangeOrderState.PartiallyFilled => ExchangeOrderStatus.PartiallyFilled,
        ExchangeOrderState.Filled => ExchangeOrderStatus.Filled,
        ExchangeOrderState.Canceled => ExchangeOrderStatus.Canceled,
        ExchangeOrderState.Rejected => ExchangeOrderStatus.Rejected,
        ExchangeOrderState.Expired => ExchangeOrderStatus.Expired,
        ExchangeOrderState.Failed => ExchangeOrderStatus.Failed,
        ExchangeOrderState.PendingCancel => ExchangeOrderStatus.PendingCancel,
        ExchangeOrderState.PendingReplace => ExchangeOrderStatus.PendingReplace,
        _ => ExchangeOrderStatus.Unknown
    };

    private static OrderState MapOrderState(ExchangeOrderState state, Order order) => state switch
    {
        ExchangeOrderState.New or ExchangeOrderState.PendingNew => OrderState.New,
        ExchangeOrderState.PartiallyFilled => OrderState.PartiallyFilled,
        ExchangeOrderState.Filled => OrderState.Filled,
        ExchangeOrderState.Canceled => OrderState.Canceled,
        ExchangeOrderState.Rejected => OrderState.Rejected,
        ExchangeOrderState.Expired => OrderState.Expired,
        ExchangeOrderState.Failed => OrderState.Failed,

        // A cancel or replace still in flight leaves the order live, so it
        // keeps whichever live state it already had.
        ExchangeOrderState.PendingCancel or ExchangeOrderState.PendingReplace =>
            order.FilledQuantity > 0m ? OrderState.PartiallyFilled : OrderState.New,
        _ => OrderState.Failed
    };

    private Task WriteAuditAsync(
        Order order,
        string action,
        string reason,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken) =>
        _audit.WriteAsync(
            new AuditEvent(
                Guid.NewGuid(),
                order.UserId,
                action,
                "Order",
                order.Id.ToString(),
                occurredAtUtc,
                before: null,
                after: reason,
                correlationId: order.ClientOrderId),
            cancellationToken);
}
