using Trading.Application.UseCases.Identity;
using Trading.Domain.Identity;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests;

public sealed class AdministratorMfaPolicyTests
{
    [Fact]
    public void AdministratorMfaPolicyRejectsActionWithoutMfa()
    {
        var user = new User(
            Guid.NewGuid(),
            "admin@example.com",
            "Admin",
            "en-US",
            "UTC",
            "USD",
            RoleType.Administrator,
            false,
            UserStatus.Active);

        var service = new AdministratorMfaPolicyService();

        Assert.Throws<InvalidOperationException>(() => service.Enforce(user));
    }

    [Fact]
    public void AdministratorMfaPolicyAllowsActionWithMfa()
    {
        var user = new User(
            Guid.NewGuid(),
            "admin-secure@example.com",
            "AdminSecure",
            "en-US",
            "UTC",
            "USD",
            RoleType.Administrator,
            true,
            UserStatus.Active);

        var service = new AdministratorMfaPolicyService();

        service.Enforce(user);

        Assert.True(user.MultiFactorAuthenticationEnabled);
    }
}
