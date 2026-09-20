using Trading.Domain.Identity;

namespace Trading.Domain.Users;

public sealed class User
{
    public User(
        Guid id,
        string email,
        string displayName,
        string locale,
        string timeZone,
        string reportingCurrency,
        RoleType role,
        bool multiFactorAuthenticationEnabled,
        UserStatus status,
        string? passwordHash = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(locale))
        {
            throw new ArgumentException("Locale is required.", nameof(locale));
        }

        if (string.IsNullOrWhiteSpace(timeZone))
        {
            throw new ArgumentException("Time zone is required.", nameof(timeZone));
        }

        if (string.IsNullOrWhiteSpace(reportingCurrency))
        {
            throw new ArgumentException("Reporting currency is required.", nameof(reportingCurrency));
        }

        Id = id;
        Email = email.Trim();
        DisplayName = displayName.Trim();
        Locale = locale.Trim();
        TimeZone = timeZone.Trim();
        ReportingCurrency = reportingCurrency.Trim();
        Role = role;
        MultiFactorAuthenticationEnabled = multiFactorAuthenticationEnabled;
        Status = status;
        PasswordHash = string.IsNullOrWhiteSpace(passwordHash) ? null : passwordHash;
    }

    public Guid Id { get; }

    public string Email { get; }

    public string DisplayName { get; }

    public string Locale { get; }

    public string TimeZone { get; }

    public string ReportingCurrency { get; }

    public RoleType Role { get; }

    public bool MultiFactorAuthenticationEnabled { get; }

    public UserStatus Status { get; }

    /// <summary>
    /// The stored password verifier. Opaque to the domain: it is produced and
    /// checked by an application-layer hasher, so no hashing algorithm is
    /// baked into the domain and the algorithm can be replaced later.
    /// </summary>
    /// <remarks>
    /// Null means the account has no password and therefore cannot sign in.
    /// That is the correct state for an invited user who has not yet set one,
    /// and it must never be read as "no password required".
    /// </remarks>
    public string? PasswordHash { get; private set; }

    /// <summary>
    /// Whether this account can be signed in to with a password at all.
    /// </summary>
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    /// <summary>
    /// Replaces the stored password verifier.
    /// </summary>
    /// <param name="passwordHash">
    /// A hash produced by the application's password hasher. This method
    /// refuses a null or blank value, because clearing a password through the
    /// same path used to set one makes it possible to disable a password by
    /// accident.
    /// </param>
    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException(
                "A password hash is required. Use RemovePassword to deliberately leave an account without one.",
                nameof(passwordHash));
        }

        PasswordHash = passwordHash;
    }

    /// <summary>
    /// Leaves the account without a password, so it cannot be signed in to
    /// until one is set again.
    /// </summary>
    public void RemovePassword() => PasswordHash = null;

    public bool RequiresMfaForAdministrator => Role == RoleType.Administrator && !MultiFactorAuthenticationEnabled;

    public void EnsureAdministratorPrivilegeAllowed()
    {
        if (Role == RoleType.Administrator && !MultiFactorAuthenticationEnabled)
        {
            throw new InvalidOperationException("Administrator actions require multi-factor authentication.");
        }
    }

    public static User CreateWithMfa(
        Guid id,
        string email,
        string displayName,
        string locale,
        string timeZone,
        string reportingCurrency,
        RoleType role,
        bool multiFactorAuthenticationEnabled,
        UserStatus status,
        string? passwordHash = null) =>
        new(
            id,
            email,
            displayName,
            locale,
            timeZone,
            reportingCurrency,
            role,
            multiFactorAuthenticationEnabled,
            status,
            passwordHash);
}
