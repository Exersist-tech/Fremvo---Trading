using Trading.Domain.Audit;
using Trading.Domain.Identity;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests
{
    public class IdentityDomainTests
    {
        [Fact]
        public void UserCreatesValidIdentityState()
        {
            var user = new User(
                Guid.NewGuid(),
                "alice@example.com",
                "Alice",
                "en-US",
                "UTC",
                "USD",
                RoleType.User,
                true,
                UserStatus.Active);

            Assert.Equal("alice@example.com", user.Email);
            Assert.Equal(RoleType.User, user.Role);
            Assert.True(user.MultiFactorAuthenticationEnabled);
        }

        [Fact]
        public void InvitationRejectsExpiredOrExhaustedCode()
        {
            var invitation = new Invitation(
                Guid.NewGuid(),
                "WELCOME-01",
                Guid.NewGuid(),
                1,
                0,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                true);

            Assert.False(invitation.IsUsableAt(DateTimeOffset.UtcNow));
        }

        [Fact]
        public void AdministratorRequiresMfaForSensitiveActions()
        {
            var administratorWithoutMfa = new User(
                Guid.NewGuid(),
                "admin@example.com",
                "Admin",
                "en-US",
                "UTC",
                "USD",
                RoleType.Administrator,
                false,
                UserStatus.Active);

            var administratorWithMfa = new User(
                Guid.NewGuid(),
                "secure-admin@example.com",
                "AdminSecure",
                "en-US",
                "UTC",
                "USD",
                RoleType.Administrator,
                true,
                UserStatus.Active);

            Assert.True(administratorWithoutMfa.RequiresMfaForAdministrator);
            Assert.Throws<InvalidOperationException>(() => administratorWithoutMfa.EnsureAdministratorPrivilegeAllowed());
            administratorWithMfa.EnsureAdministratorPrivilegeAllowed();
        }

        [Fact]
        public void AuditEventRequiresCorrelationIdentifierOrCreatesOne()
        {
            var auditEvent = new AuditEvent(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "User.Registered",
                "User",
                "abc",
                DateTimeOffset.UtcNow,
                null,
                null,
                null);

            Assert.NotNull(auditEvent.CorrelationId);
        }
    }
}
