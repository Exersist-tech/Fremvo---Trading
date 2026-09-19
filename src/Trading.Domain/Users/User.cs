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
        UserStatus status)
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
        UserStatus status) =>
        new(
            id,
            email,
            displayName,
            locale,
            timeZone,
            reportingCurrency,
            role,
            multiFactorAuthenticationEnabled,
            status);
}
