using Trading.Domain.Users;

namespace Trading.Application.UseCases.Identity;

/// <summary>
/// Why a sign-in attempt did not succeed.
/// </summary>
/// <remarks>
/// These reasons exist for audit and diagnostics. They must not be returned
/// to the browser: telling an unauthenticated caller the difference between
/// "no such account", "wrong password" and "suspended" reveals who holds an
/// account on an invitation-only platform.
/// </remarks>
public enum AuthenticationFailureReason
{
    None = 0,
    UnknownAccount,
    NoPasswordSet,
    IncorrectPassword,
    AccountSuspended,
}

public sealed class AuthenticationOutcome
{
    private AuthenticationOutcome(
        bool succeeded,
        AuthenticationFailureReason reason,
        bool passwordNeedsRehash)
    {
        Succeeded = succeeded;
        FailureReason = reason;
        PasswordNeedsRehash = passwordNeedsRehash;
    }

    public bool Succeeded { get; }

    public AuthenticationFailureReason FailureReason { get; }

    /// <summary>
    /// The password was correct but its stored hash uses superseded
    /// parameters. The caller should re-hash and save it.
    /// </summary>
    public bool PasswordNeedsRehash { get; }

    public static AuthenticationOutcome Success(bool passwordNeedsRehash = false) =>
        new(true, AuthenticationFailureReason.None, passwordNeedsRehash);

    public static AuthenticationOutcome Failure(AuthenticationFailureReason reason) =>
        new(false, reason, false);
}

public interface IAuthenticationService
{
    /// <summary>
    /// Verifies a password against an account.
    /// </summary>
    /// <param name="user">
    /// The account, or null when no account matched the supplied address. A
    /// null user is handled here rather than short-circuited by the caller so
    /// that the same work is done either way.
    /// </param>
    AuthenticationOutcome Authenticate(User? user, string? password);
}

/// <summary>
/// Verifies a password against the account's stored hash.
/// </summary>
/// <remarks>
/// <para>
/// This replaces an earlier implementation that checked only that the
/// password was at least eight characters long, which meant any string of
/// sufficient length signed in as any active user. No password was stored or
/// compared at all.
/// </para>
/// <para>
/// Every path through this method performs one password verification,
/// including the paths for an unknown account and an account with no password
/// set. Returning early on those would make them measurably faster than a
/// wrong password against a real account, and that difference discloses which
/// addresses are registered.
/// </para>
/// <para>
/// The suspended check happens only after the password is verified, so a
/// caller who does not know the password cannot learn that an address exists
/// but is suspended.
/// </para>
/// </remarks>
public sealed class AuthenticationService : IAuthenticationService
{
    private readonly Pbkdf2PasswordHasher _hasher;

    public AuthenticationService(Pbkdf2PasswordHasher hasher) =>
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));

    public AuthenticationOutcome Authenticate(User? user, string? password)
    {
        // An account that does not exist, and one that exists without a
        // password, are both verified against a decoy hash so they cost the
        // same as a real attempt.
        var storedHash = user?.PasswordHash ?? _hasher.DecoyHash;

        var verification = _hasher.Verify(storedHash, password);

        if (user is null)
        {
            return AuthenticationOutcome.Failure(AuthenticationFailureReason.UnknownAccount);
        }

        if (!user.HasPassword)
        {
            // An invited user who has not set a password cannot sign in. This
            // must never fall through to success.
            return AuthenticationOutcome.Failure(AuthenticationFailureReason.NoPasswordSet);
        }

        if (verification == PasswordVerification.Failed)
        {
            return AuthenticationOutcome.Failure(AuthenticationFailureReason.IncorrectPassword);
        }

        if (user.Status != UserStatus.Active)
        {
            return AuthenticationOutcome.Failure(AuthenticationFailureReason.AccountSuspended);
        }

        return AuthenticationOutcome.Success(verification == PasswordVerification.SuccessRehashNeeded);
    }
}
