using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Binance;

namespace Trading.ArchitectureTests;

public sealed class BinancePermissionValidatorTests
{
    [Fact]
    public void BinanceValidatorAcceptsReadAndTradePermissionsWithoutWithdrawalRights()
    {
        var validator = new BinancePermissionValidator();

        var permissions = validator.Validate("api-key", "api-secret", "testnet");

        Assert.True(permissions.CanRead);
        Assert.True(permissions.CanTrade);
        Assert.False(permissions.CanWithdraw);
        Assert.True(permissions.AllowsTrading);
    }

    [Fact]
    public void BinanceValidatorRequiresKeyAndSecret()
    {
        var validator = new BinancePermissionValidator();

        Assert.Throws<ArgumentException>(() => validator.Validate(string.Empty, "api-secret", "testnet"));
        Assert.Throws<ArgumentException>(() => validator.Validate("api-key", string.Empty, "testnet"));
    }
}