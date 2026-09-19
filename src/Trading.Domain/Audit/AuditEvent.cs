namespace Trading.Domain.Audit;

public sealed class AuditEvent
{
    public AuditEvent(
        Guid id,
        Guid? actorUserId,
        string action,
        string targetType,
        string targetId,
        DateTimeOffset occurredAtUtc,
        string? before,
        string? after,
        string? correlationId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Audit event id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("Action is required.", nameof(action));
        }

        if (string.IsNullOrWhiteSpace(targetType))
        {
            throw new ArgumentException("Target type is required.", nameof(targetType));
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new ArgumentException("Target id is required.", nameof(targetId));
        }

        if (occurredAtUtc == default)
        {
            throw new ArgumentException("Occurrence time is required.", nameof(occurredAtUtc));
        }

        Id = id;
        ActorUserId = actorUserId;
        Action = action.Trim();
        TargetType = targetType.Trim();
        TargetId = targetId.Trim();
        OccurredAtUtc = occurredAtUtc;
        Before = before;
        After = after;
        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString() : correlationId.Trim();
    }

    public Guid Id { get; }

    public Guid? ActorUserId { get; }

    public string Action { get; }

    public string TargetType { get; }

    public string TargetId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string? Before { get; }

    public string? After { get; }

    public string CorrelationId { get; }
}
