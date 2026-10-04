namespace Trading.Application.UseCases.Identity;

public sealed record InvitationRequest(
    string Email,
    string? OneTimeCode = null);
