namespace Trading.Domain.Users;

public sealed class Invitation
{
    // EF must hydrate pre-migration invitations with a null recipient so they can be rejected.
    private Invitation() => Code = null!;

    public Invitation(
        Guid id,
        string code,
        string inviteeEmail,
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

        if (code is not { Length: 64 }
            || code.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
        {
            throw new ArgumentException("An uppercase SHA-256 invitation code digest is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(inviteeEmail) || inviteeEmail.Length > 256
            || !System.Net.Mail.MailAddress.TryCreate(inviteeEmail.Trim(), out var recipient)
            || !string.Equals(recipient.Address, inviteeEmail.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid recipient email is required.", nameof(inviteeEmail));
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

        if (expiresAtUtc == default || expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC expiration is required.", nameof(expiresAtUtc));
        }

        Id = id;
        Code = code;
        RecipientEmail = inviteeEmail.Trim();
        IssuedByUserId = issuedByUserId;
        MaxUses = maxUses;
        UsedCount = usedCount;
        ExpiresAtUtc = expiresAtUtc;
        IsActive = isActive;
    }

    public Guid Id { get; }

    /// <summary>SHA-256 digest of the high-entropy one-time code, never the bearer value.</summary>
    public string Code { get; }

    // Null is reserved for invitations created before recipient binding was introduced.
    public string? RecipientEmail { get; }

    public Guid IssuedByUserId { get; }

    public int MaxUses { get; }

    public int UsedCount { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsActive { get; }

    public bool IsExpiredAt(DateTimeOffset nowUtc) => nowUtc >= ExpiresAtUtc;

    public bool IsUsableAt(DateTimeOffset nowUtc) =>
        IsActive && !IsExpiredAt(nowUtc) && UsedCount < MaxUses && !string.IsNullOrWhiteSpace(RecipientEmail);

    public bool IsIssuedTo(string? email) =>
        !string.IsNullOrWhiteSpace(RecipientEmail)
        && !string.IsNullOrWhiteSpace(email)
        && string.Equals(RecipientEmail, email.Trim(), StringComparison.OrdinalIgnoreCase);

    public Invitation Consume(DateTimeOffset nowUtc)
    {
        if (nowUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Redemption time must be UTC.", nameof(nowUtc));
        if (!IsUsableAt(nowUtc))
        {
            throw new InvalidOperationException("Invitation is not usable.");
        }

        return new Invitation(Id, Code, RecipientEmail!, IssuedByUserId, MaxUses, UsedCount + 1, ExpiresAtUtc, IsActive);
    }
}
