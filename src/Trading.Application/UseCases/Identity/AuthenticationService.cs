using Trading.Domain.Users;

namespace Trading.Application.UseCases.Identity;

public interface IAuthenticationService
{
    bool ValidateCredentials(User user, string password);
}

public sealed class AuthenticationService : IAuthenticationService
{
    public bool ValidateCredentials(User user, string password)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        return user.Status == UserStatus.Active && password.Length >= 8;
    }
}
