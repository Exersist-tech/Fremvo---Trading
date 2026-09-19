using Trading.Exchanges.Abstractions;
using Trading.Infrastructure.Secrets;

namespace Trading.ArchitectureTests;

public sealed class ExchangeAbstractionTests
{
    [Fact]
    public void ExchangeAccountProvidesNeutralStatusTransitions()
    {
        var now = DateTimeOffset.UtcNow;
        var account = new ExchangeAccount(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ExchangeKind.Binance,
            "Primary account",
            "secret-ref/binance-primary",
            now);

        Assert.Equal(ExchangeAccountStatus.Disconnected, account.Status);
        Assert.False(account.CanTrade);

        account.MarkPendingValidation(now.AddMinutes(1));
        Assert.Equal(ExchangeAccountStatus.PendingValidation, account.Status);

        account.MarkConnected(now.AddMinutes(2));
        Assert.True(account.CanTrade);

        account.MarkSuspended(now.AddMinutes(3));
        Assert.Equal(ExchangeAccountStatus.Suspended, account.Status);
        Assert.False(account.CanTrade);
    }

    [Fact]
    public void SecretReferenceModelIsSafeMetadataOnly()
    {
        var reference = new SecretReference("crypto/binance/api-key", "v2");

        Assert.Equal("crypto/binance/api-key", reference.SecretName);
        Assert.Equal("v2", reference.Version);
    }
}
