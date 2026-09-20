using System.Security.Claims;
using Trading.Domain.Identity;

namespace Trading.Web.Security;

/// <summary>
/// Reads the signed-in user from the request principal. Every user-scoped endpoint must obtain the
/// owning user id from here and never from the request body or query string, so one user can never
/// address another user's data by supplying a different id.
/// </summary>
internal static class CurrentUser
{
    public const string UserIdClaim = "trading:user-id";

    public static Guid? TryGetUserId(ClaimsPrincipal? principal)
    {
        var value = principal?.FindFirstValue(UserIdClaim);

        return Guid.TryParse(value, out var userId) && userId != Guid.Empty
            ? userId
            : null;
    }

    public static bool IsAdministrator(ClaimsPrincipal? principal) =>
        principal?.IsInRole(nameof(RoleType.Administrator)) == true;
}
