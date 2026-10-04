namespace Trading.Application.UseCases.Identity;

public sealed record LoginRequest(string Email, string Password, string? OneTimeCode = null);
