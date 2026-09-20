using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Kraken;

namespace Trading.ArchitectureTests;

public sealed class KrakenPermissionValidatorTests
{
    [Fact]
    public void KrakenValidatorAcceptsReadAndTradePermissionsWithoutWithdrawalRights()
    {
        var validator = new KrakenPermissionValidator();

        var permissions = validator.Validate("api-key", "api-secret", "proving");

        Assert.True(permissions.CanRead);
        Assert.True(permissions.CanTrade);
        Assert.False(permissions.CanWithdraw);
        Assert.True(permissions.AllowsTrading);
    }

    [Fact]
    public void KrakenValidatorRequiresKeyAndSecret()
    {
        var validator = new KrakenPermissionValidator();

        Assert.Throws<ArgumentException>(() => validator.Validate(string.Empty, "api-secret", "proving"));
        Assert.Throws<ArgumentException>(() => validator.Validate("api-key", string.Empty, "proving"));
    }
}