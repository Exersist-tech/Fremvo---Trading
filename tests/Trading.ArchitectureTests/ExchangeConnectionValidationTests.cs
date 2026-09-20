using Trading.Application.UseCases.Exchange;
using Trading.Exchanges.Abstractions;

namespace Trading.ArchitectureTests;

public sealed class ExchangeConnectionValidationTests
{
    [Fact]
    public void ExchangeAccountServiceRejectsWithdrawPermission()
    {
        var service = new ExchangeAccountService();
        var account = service.RegisterNewAccount(
            Guid.NewGuid(),
            new ExchangeAccountRequest(
                Guid.NewGuid(),
                ExchangeKind.Kraken,
                "Primary account",
                "kv/kraken/primary-api-key"),
            DateTimeOffset.UtcNow);

        var permissions = new ApiPermissionSnapshot(true, true, true, DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => service.ValidateConnection(account, permissions, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ExchangeAccountServiceAcceptsReadAndTradeWithoutWithdrawPermission()
    {
        var service = new ExchangeAccountService();
        var account = service.RegisterNewAccount(
            Guid.NewGuid(),
            new ExchangeAccountRequest(
                Guid.NewGuid(),
                ExchangeKind.Kraken,
                "Primary account",
                "kv/kraken/primary-api-key"),
            DateTimeOffset.UtcNow);

        var permissions = new ApiPermissionSnapshot(true, true, false, DateTimeOffset.UtcNow);

        var validated = service.ValidateConnection(account, permissions, DateTimeOffset.UtcNow);

        Assert.Equal(ExchangeAccountStatus.Connected, validated.Status);
        Assert.True(validated.CanTrade);
    }

    [Fact]
    public void ExchangeAccountServiceRejectsStaleValidation()
    {
        var service = new ExchangeAccountService();
        var account = service.RegisterNewAccount(
            Guid.NewGuid(),
            new ExchangeAccountRequest(
                Guid.NewGuid(),
                ExchangeKind.Kraken,
                "Primary account",
                "kv/kraken/primary-api-key"),
            DateTimeOffset.UtcNow);

        var permissions = new ApiPermissionSnapshot(true, true, false, DateTimeOffset.UtcNow.AddDays(-2));

        Assert.Throws<InvalidOperationException>(() => service.ValidateConnection(account, permissions, DateTimeOffset.UtcNow, TimeSpan.FromHours(24)));
    }
}
