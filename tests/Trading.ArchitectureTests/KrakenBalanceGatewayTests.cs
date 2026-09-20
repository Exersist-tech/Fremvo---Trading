using Trading.Exchanges.Kraken.Account;

namespace Trading.ArchitectureTests;

public sealed class KrakenBalanceGatewayTests
{
    [Fact]
    public void ParseNormalizesKrakenAssetCodes()
    {
        var at = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        var snapshot = KrakenBalanceGateway.Parse(
            """{"error":[],"result":{"XXBT":"1.2500","ZUSD":"42.10","XDG":"7"}}""", at);

        Assert.Equal(at, snapshot.RetrievedAtUtc);
        Assert.Collection(snapshot.Balances,
            balance => { Assert.Equal("BTC", balance.Asset); Assert.Equal(1.25m, balance.Total); },
            balance => { Assert.Equal("USD", balance.Asset); Assert.Equal(42.10m, balance.Total); },
            balance => { Assert.Equal("DOGE", balance.Asset); Assert.Equal(7m, balance.Total); });
    }

    [Fact]
    public void ParseRefusesAnExchangeErrorInsteadOfReturningBalances()
    {
        Assert.Throws<Trading.Exchanges.Abstractions.Account.ExchangeBalanceReadException>(() =>
            KrakenBalanceGateway.Parse(
                """{"error":["EAPI:Invalid key"],"result":{"XXBT":"1"}}""",
                DateTimeOffset.UtcNow));
    }
}
