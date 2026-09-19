using Trading.Application.UseCases.Administration;
using Trading.Application.UseCases.Identity;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests
{
    public class ApplicationIdentityTests
    {
        [Fact]
        public void InvitationServiceCreatesValidInvitation()
        {
            var service = new InvitationService();
            var invitation = service.CreateInvitation(
                Guid.NewGuid(),
                "WELCOME-10",
                3,
                DateTimeOffset.UtcNow.AddDays(7));

            Assert.Equal("WELCOME-10", invitation.Code);
            Assert.Equal(0, invitation.UsedCount);
            Assert.True(invitation.IsActive);
        }

        [Fact]
        public void PlanCapturesEntitlementFlags()
        {
            var plan = new Plan(
                Guid.NewGuid(),
                "trial",
                3,
                false,
                false);

            Assert.Equal(3, plan.MaxExperimentWorkers);
            Assert.False(plan.LiveTradingEnabled);
            Assert.False(plan.FuturesEnabled);
        }
    }
}
