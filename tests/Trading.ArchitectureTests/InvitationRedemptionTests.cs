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
            "WELCOME-01",
            Guid.NewGuid(),
            2,
            0,
            DateTimeOffset.UtcNow.AddDays(7),
            true);

        var redeemed = service.RedeemInvitation(invitation, DateTimeOffset.UtcNow);

        Assert.Equal(1, redeemed.UsedCount);
        Assert.True(redeemed.IsUsableAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void InvitationServiceRejectsExpiredOrExhaustedInvitation()
    {
        var service = new InvitationService();
        var expiredInvitation = new Invitation(
            Guid.NewGuid(),
            "EXPIRED-99",
            Guid.NewGuid(),
            1,
            0,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            true);

        var exhaustedInvitation = new Invitation(
            Guid.NewGuid(),
            "USED-OUT",
            Guid.NewGuid(),
            1,
            1,
            DateTimeOffset.UtcNow.AddDays(1),
            true);

        Assert.Throws<InvalidOperationException>(() => service.RedeemInvitation(expiredInvitation, DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => service.RedeemInvitation(exhaustedInvitation, DateTimeOffset.UtcNow));
    }
}
