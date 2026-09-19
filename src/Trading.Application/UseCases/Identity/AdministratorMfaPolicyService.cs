using Trading.Domain.Users;

namespace Trading.Application.UseCases.Identity;

public interface IAdministratorMfaPolicyService
{
    void Enforce(User user);
}

public sealed class AdministratorMfaPolicyService : IAdministratorMfaPolicyService
{
    public void Enforce(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.EnsureAdministratorPrivilegeAllowed();
    }
}
