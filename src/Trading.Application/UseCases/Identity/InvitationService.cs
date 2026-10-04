using Trading.Domain.Users;

namespace Trading.Application.UseCases.Identity;

public interface IInvitationService
{
    Invitation CreateInvitation(Guid issuerUserId, string code, string recipientEmail, int maxUses, DateTimeOffset expiresAtUtc);

    Invitation RedeemInvitation(Invitation invitation, string recipientEmail, DateTimeOffset nowUtc);
}

public sealed class InvitationService : IInvitationService
{
    public Invitation CreateInvitation(Guid issuerUserId, string code, string recipientEmail, int maxUses, DateTimeOffset expiresAtUtc)
    {
        if (issuerUserId == Guid.Empty)
        {
            throw new ArgumentException("Issuer is required.", nameof(issuerUserId));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code is required.", nameof(code));
        }

        if (maxUses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUses), "Max uses must be positive.");
        }

        if (expiresAtUtc == default)
        {
            throw new ArgumentException("Expiration time is required.", nameof(expiresAtUtc));
        }

        return new Invitation(
            Guid.NewGuid(),
            InvitationCodeDigest.Compute(code),
            recipientEmail,
            issuerUserId,
            maxUses,
            0,
            expiresAtUtc,
            true);
    }

    public Invitation RedeemInvitation(Invitation invitation, string recipientEmail, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        if (!invitation.IsUsableAt(nowUtc) || !invitation.IsIssuedTo(recipientEmail))
        {
            throw new InvalidOperationException("Invitation is not valid for redemption at the current time.");
        }

        return invitation.Consume(nowUtc);
    }
}
