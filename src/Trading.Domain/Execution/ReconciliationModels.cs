namespace Trading.Domain.Execution;

public enum ExchangeOrderStatus
{
    Unknown = 0,
    New,
    PendingNew,
    PartiallyFilled,
    Filled,
    Canceled,
    Rejected,
    Expired,
    Failed,
    PendingCancel,
    PendingReplace
}

public sealed class OrderReconciliationRecord
{
    public OrderReconciliationRecord(
        Guid id,
        Guid orderId,
        string? exchangeOrderId,
        ExchangeOrderStatus observedStatus,
        DateTimeOffset observedAtUtc,
        string source,
        string? resolutionReason = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Reconciliation record id is required.", nameof(id));
        }

        if (orderId == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(orderId));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Source is required.", nameof(source));
        }

        Id = id;
        OrderId = orderId;
        ExchangeOrderId = exchangeOrderId;
        ObservedStatus = observedStatus;
        ObservedAtUtc = observedAtUtc;
        Source = source.Trim();
        ResolutionReason = resolutionReason;
        ResolvedAtUtc = null;
    }

    public Guid Id { get; }

    public Guid OrderId { get; }

    public string? ExchangeOrderId { get; }

    public ExchangeOrderStatus ObservedStatus { get; private set; }

    public DateTimeOffset ObservedAtUtc { get; }

    public string Source { get; }

    public string? ResolutionReason { get; private set; }

    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    public bool RequiresManualReview => ObservedStatus == ExchangeOrderStatus.Unknown || ObservedStatus == ExchangeOrderStatus.Failed;

    public bool IsResolved => ResolvedAtUtc.HasValue;

    /// <summary>
    /// True only when the exchange has proven the order is not live and can
    /// never become live.
    /// </summary>
    /// <remarks>
    /// <see cref="ExchangeOrderStatus.Failed"/> is deliberately excluded. A
    /// failure is a failure to learn the outcome, not evidence that the
    /// exchange rejected the order.
    /// </remarks>
    public bool ProvesOrderIsNotLive =>
        ObservedStatus is ExchangeOrderStatus.Rejected
            or ExchangeOrderStatus.Expired
            or ExchangeOrderStatus.Canceled;

    /// <summary>
    /// True whenever resubmitting would risk duplicating live exposure.
    /// </summary>
    /// <remarks>
    /// The default is to block. Resubmission is permitted only by positive
    /// proof that the original order is not live, never by the absence of
    /// evidence that it is.
    /// </remarks>
    public bool RequiresResolutionBeforeResubmission => !ProvesOrderIsNotLive;

    public void Resolve(ExchangeOrderStatus resolvedStatus, string reason, DateTimeOffset resolvedAtUtc)
    {
        if (resolvedStatus == ExchangeOrderStatus.Unknown)
        {
            throw new ArgumentException("A resolved status cannot be unknown.", nameof(resolvedStatus));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Resolution reason is required.", nameof(reason));
        }

        ObservedStatus = resolvedStatus;
        ResolutionReason = reason.Trim();
        ResolvedAtUtc = resolvedAtUtc;
    }
}
