namespace Trading.Domain.Users;

public sealed class Invitation
{
    public Invitation(
        Guid id,
        string code,
        Guid issuedByUserId,
        int maxUses,
        int usedCount,
        DateTimeOffset expiresAtUtc,
        bool isActive)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Invitation id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Invitation code is required.", nameof(code));
        }

        if (issuedByUserId == Guid.Empty)
        {
            throw new ArgumentException("Issuer is required.", nameof(issuedByUserId));
        }

        if (maxUses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUses), "Maximum uses must be positive.");
        }

        if (usedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usedCount), "Used count cannot be negative.");
        }

        if (usedCount > maxUses)
        {
            throw new ArgumentOutOfRangeException(nameof(usedCount), "Used count cannot exceed maximum uses.");
        }

        if (expiresAtUtc == default)
        {
            throw new ArgumentException("Expiration is required.", nameof(expiresAtUtc));
        }

        Id = id;
        Code = code.Trim();
        IssuedByUserId = issuedByUserId;
        MaxUses = maxUses;
        UsedCount = usedCount;
        ExpiresAtUtc = expiresAtUtc;
        IsActive = isActive;
    }

    public Guid Id { get; }

    public string Code { get; }

    public Guid IssuedByUserId { get; }

    public int MaxUses { get; }

    public int UsedCount { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsActive { get; }

    public bool IsExpiredAt(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public bool IsUsableAt(DateTimeOffset nowUtc) => IsActive && !IsExpiredAt(nowUtc) && UsedCount < MaxUses;

    public Invitation Consume()
    {
        if (!IsUsableAt(DateTime.UtcNow))
        {
            throw new InvalidOperationException("Invitation is not usable.");
        }

        return new Invitation(Id, Code, IssuedByUserId, MaxUses, UsedCount + 1, ExpiresAtUtc, IsActive);
    }
}
