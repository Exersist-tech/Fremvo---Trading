using Trading.Exchanges.Abstractions;

namespace Trading.Application.UseCases.Exchange;

public sealed record ExchangeAccountRequest(
    Guid UserId,
    ExchangeKind ExchangeKind,
    string DisplayName,
    string CredentialReference);
