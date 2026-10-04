namespace Trading.Domain.Entitlements;

public enum EntitlementStatus { Active = 0, Revoked = 1 }

public sealed class Entitlement
{
    public Entitlement(Guid id, Guid userId, Guid planId, DateTimeOffset assignedAtUtc,
        DateTimeOffset? trialExpiresAtUtc = null, EntitlementStatus status = EntitlementStatus.Active)
    {
        if (id == Guid.Empty || userId == Guid.Empty || planId == Guid.Empty)
            throw new ArgumentException("Entitlement, user and plan identifiers are required.");
        if (assignedAtUtc.Offset != TimeSpan.Zero
            || trialExpiresAtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
            throw new ArgumentException("Entitlement timestamps must be UTC.");
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        Id = id;
        UserId = userId;
        PlanId = planId;
        AssignedAtUtc = assignedAtUtc;
        TrialExpiresAtUtc = trialExpiresAtUtc;
        Status = status;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public Guid PlanId { get; }
    public DateTimeOffset AssignedAtUtc { get; }
    public DateTimeOffset? TrialExpiresAtUtc { get; private set; }
    public EntitlementStatus Status { get; private set; }

    public bool IsEffectiveAt(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Entitlement evaluation requires a UTC instant.", nameof(nowUtc));
        return Status == EntitlementStatus.Active && nowUtc >= AssignedAtUtc
            && (TrialExpiresAtUtc is null || nowUtc < TrialExpiresAtUtc);
    }

    public void Revoke()
    {
        if (Status != EntitlementStatus.Active)
            throw new InvalidOperationException("Only an active entitlement may be revoked.");
        Status = EntitlementStatus.Revoked;
    }

    public void ExtendTrial(DateTimeOffset nowUtc, DateTimeOffset newExpiryUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero || newExpiryUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Trial renewal requires UTC instants.");
        if (Status != EntitlementStatus.Active || TrialExpiresAtUtc is null)
            throw new InvalidOperationException("Only an active expiring trial can be renewed.");
        if (newExpiryUtc <= nowUtc || newExpiryUtc <= TrialExpiresAtUtc)
            throw new ArgumentOutOfRangeException(nameof(newExpiryUtc),
                "The new trial expiry must be future and later than its current expiry.");
        TrialExpiresAtUtc = newExpiryUtc;
    }
}
