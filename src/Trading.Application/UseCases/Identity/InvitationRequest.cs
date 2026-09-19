namespace Trading.Application.UseCases.Identity;

public sealed record InvitationRequest(
    string Email,
    string DisplayName,
    string Locale,
    string TimeZone,
    string ReportingCurrency);
