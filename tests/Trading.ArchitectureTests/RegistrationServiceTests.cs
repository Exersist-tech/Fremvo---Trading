using Trading.Application.UseCases.Identity;
using Trading.Domain.Identity;

namespace Trading.ArchitectureTests;

public sealed class RegistrationServiceTests
{
    [Fact]
    public void RegistrationServiceRequiresStrongPasswordAndRequiredProfileData()
    {
        var service = new RegistrationService(new Pbkdf2PasswordHasher());
        var request = new RegisterUserRequest(
            "alice@example.com",
            "Alice",
            "en-US",
            "UTC",
            "USD",
            "WELCOME-01",
            "StrongPass123");

        var user = service.Register(request, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal(RoleType.User, user.Role);
        Assert.False(user.MultiFactorAuthenticationEnabled);
    }

    [Fact]
    public void RegistrationServiceRejectsShortPassword()
    {
        var service = new RegistrationService(new Pbkdf2PasswordHasher());
        var request = new RegisterUserRequest(
            "alice@example.com",
            "Alice",
            "en-US",
            "UTC",
            "USD",
            "WELCOME-01",
            "short");

        Assert.Throws<ArgumentException>(() => service.Register(request, Guid.NewGuid(), Guid.NewGuid()));
    }
}
