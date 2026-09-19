using Trading.Application.UseCases.Identity;
using Trading.Domain.Identity;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests;

public sealed class AuthenticationServiceTests
{
    [Fact]
    public void AuthenticationServiceAcceptsActiveUserWithStrongPassword()
    {
        var user = new User(
            Guid.NewGuid(),
            "alice@example.com",
            "Alice",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            false,
            UserStatus.Active);

        var service = new AuthenticationService();

        Assert.True(service.ValidateCredentials(user, "StrongPass123"));
    }

    [Fact]
    public void AuthenticationServiceRejectsInactiveUserOrShortPassword()
    {
        var activeUser = new User(
            Guid.NewGuid(),
            "bob@example.com",
            "Bob",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            false,
            UserStatus.Active);

        var suspendedUser = new User(
            Guid.NewGuid(),
            "carol@example.com",
            "Carol",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            false,
            UserStatus.Suspended);

        var service = new AuthenticationService();

        Assert.False(service.ValidateCredentials(activeUser, "short"));
        Assert.False(service.ValidateCredentials(suspendedUser, "StrongPass123"));
    }
}
