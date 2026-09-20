namespace Trading.Application.UseCases.Identity;

public sealed record RegisterUserRequest(
    string Email,
    string DisplayName,
    string Locale,
    string TimeZone,
    string ReportingCurrency,
    string InvitationCode,
    string Password);
