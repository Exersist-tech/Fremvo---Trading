using Trading.Application.UseCases.Identity;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests;

public sealed class InvitationRedemptionTests
{
    [Fact]
    public void InvitationServiceRedeemsValidInvitationOnce()
    {
        var service = new InvitationService();
        var invitation = new Invitation(
            Guid.NewGuid(),
            InvitationCodeDigest.Compute("WELCOME-01"),
            "new@example.test",
            Guid.NewGuid(),
            2,
            0,
            DateTimeOffset.UtcNow.AddDays(7),
            true);

        Assert.True(invitation.IsIssuedTo(" NEW@EXAMPLE.TEST "));
        Assert.False(invitation.IsIssuedTo("other@example.test"));
        Assert.Throws<InvalidOperationException>(() =>
            service.RedeemInvitation(invitation, "other@example.test", DateTimeOffset.UtcNow));
        var redeemed = service.RedeemInvitation(invitation, " NEW@EXAMPLE.TEST ", DateTimeOffset.UtcNow);

        Assert.Equal(1, redeemed.UsedCount);
        Assert.Equal("new@example.test", redeemed.RecipientEmail);
        Assert.True(redeemed.IsUsableAt(DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => invitation.Consume(
            DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<InvalidOperationException>(() =>
            invitation.Consume(invitation.ExpiresAtUtc));
    }

    [Fact]
    public void InvitationServiceRejectsExpiredOrExhaustedInvitation()
    {
        var service = new InvitationService();
        var expiredInvitation = new Invitation(
            Guid.NewGuid(),
            InvitationCodeDigest.Compute("EXPIRED-99"),
            "new@example.test",
            Guid.NewGuid(),
            1,
            0,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            true);

        var exhaustedInvitation = new Invitation(
            Guid.NewGuid(),
            InvitationCodeDigest.Compute("USED-OUT"),
            "new@example.test",
            Guid.NewGuid(),
            1,
            1,
            DateTimeOffset.UtcNow.AddDays(1),
            true);

        Assert.Throws<InvalidOperationException>(() => service.RedeemInvitation(expiredInvitation, "new@example.test", DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => service.RedeemInvitation(exhaustedInvitation, "new@example.test", DateTimeOffset.UtcNow));
    }
}
