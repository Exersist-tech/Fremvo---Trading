using Trading.Exchanges.Abstractions;

namespace Trading.Application.UseCases.Exchange;

public interface IExchangeAccountService
{
    ExchangeAccount RegisterNewAccount(Guid userId, ExchangeAccountRequest request, DateTimeOffset nowUtc);

    ExchangeAccount ValidateConnection(ExchangeAccount account, ApiPermissionSnapshot permissions, DateTimeOffset nowUtc, TimeSpan? maxAge = null);

    ExchangeAccount Disconnect(ExchangeAccount account, DateTimeOffset nowUtc);
}

public sealed class ExchangeAccountService : IExchangeAccountService
{
    public ExchangeAccount RegisterNewAccount(Guid userId, ExchangeAccountRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ArgumentException("Display name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CredentialReference))
        {
            throw new ArgumentException("Credential reference is required.", nameof(request));
        }

        return new ExchangeAccount(
            Guid.NewGuid(),
            userId,
            request.ExchangeKind,
            request.DisplayName.Trim(),
            request.CredentialReference.Trim(),
            nowUtc);
    }

    public ExchangeAccount ValidateConnection(ExchangeAccount account, ApiPermissionSnapshot permissions, DateTimeOffset nowUtc, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(permissions);

        if (!permissions.AllowsTrading)
        {
            throw new InvalidOperationException("Exchange account validation failed: read and trade permissions are required and withdraw permission is not allowed.");
        }

        var effectiveMaxAge = maxAge ?? TimeSpan.FromHours(24);
        if (permissions.IsStale(effectiveMaxAge, nowUtc))
        {
            throw new InvalidOperationException("Exchange account validation is stale and must be refreshed before trading.");
        }

        account.MarkConnected(nowUtc);
        return account;
    }

    public ExchangeAccount Disconnect(ExchangeAccount account, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(account);

        account.MarkDisconnected();
        return account;
    }
}
