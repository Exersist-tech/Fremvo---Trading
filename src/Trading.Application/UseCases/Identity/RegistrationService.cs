using Trading.Domain.Identity;
using Trading.Domain.Users;

namespace Trading.Application.UseCases.Identity;

public interface IRegistrationService
{
    User Register(RegisterUserRequest request, Guid invitationId, Guid userId);
}

public sealed class RegistrationService : IRegistrationService
{
    private readonly IPasswordHasher _passwordHasher;

    public RegistrationService(IPasswordHasher passwordHasher) =>
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));

    public User Register(RegisterUserRequest request, Guid invitationId, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Email is not { Length: > 0 })
        {
            throw new ArgumentException("Email is required.", nameof(request));
        }

        if (request.DisplayName is not { Length: > 0 })
        {
            throw new ArgumentException("Display name is required.", nameof(request));
        }

        if (request.Locale is not { Length: > 0 })
        {
            throw new ArgumentException("Locale is required.", nameof(request));
        }

        if (request.TimeZone is not { Length: > 0 })
        {
            throw new ArgumentException("Time zone is required.", nameof(request));
        }

        if (request.ReportingCurrency is not { Length: > 0 })
        {
            throw new ArgumentException("Reporting currency is required.", nameof(request));
        }

        if (request.InvitationCode is not { Length: > 0 })
        {
            throw new ArgumentException("Invitation code is required.", nameof(request));
        }

        if (request.Password is not { Length: >= 8 })
        {
            throw new ArgumentException("Password must be at least 8 characters long.", nameof(request));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (invitationId == Guid.Empty)
        {
            throw new ArgumentException("Invitation id is required.", nameof(invitationId));
        }

        return new User(
            userId,
            request.Email.Trim(),
            request.DisplayName.Trim(),
            request.Locale.Trim(),
            request.TimeZone.Trim(),
            request.ReportingCurrency.Trim(),
            RoleType.User,
            false,
            UserStatus.Active,
            // The password was previously validated and then discarded, so a
            // registered account held no credential at all. It is hashed here;
            // the plaintext is never stored and never leaves this method.
            passwordHash: _passwordHasher.Hash(request.Password));
    }
}
