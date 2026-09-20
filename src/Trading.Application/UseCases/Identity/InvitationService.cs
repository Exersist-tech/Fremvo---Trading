using Trading.Domain.Users;

namespace Trading.Application.UseCases.Identity;

public interface IInvitationService
{
    Invitation CreateInvitation(Guid issuerUserId, string code, int maxUses, DateTimeOffset expiresAtUtc);

    Invitation RedeemInvitation(Invitation invitation, DateTimeOffset nowUtc);
}

public sealed class InvitationService : IInvitationService
{
    public Invitation CreateInvitation(Guid issuerUserId, string code, int maxUses, DateTimeOffset expiresAtUtc)
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
            code.Trim(),
            issuerUserId,
            maxUses,
            0,
            expiresAtUtc,
            true);
    }

    public Invitation RedeemInvitation(Invitation invitation, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        if (!invitation.IsUsableAt(nowUtc))
        {
            throw new InvalidOperationException("Invitation is not valid for redemption at the current time.");
        }

        return invitation.Consume();
    }
}
