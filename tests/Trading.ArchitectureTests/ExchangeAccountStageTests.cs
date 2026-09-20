using Trading.Exchanges.Abstractions;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers the trading stage on an exchange account: paper by default, no
/// skipping stages, no promotion of an unhealthy connection, and an always
/// available route back to simulated money.
/// </summary>
public sealed class ExchangeAccountStageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewAccountStartsInPaperAndCannotReachTheExchange()
    {
        var account = NewAccount();

        Assert.Equal(TradingStage.Paper, account.Stage);
        Assert.False(account.CanReachExchange);
    }

    [Fact]
    public void AConnectedPaperAccountStillCannotReachTheExchange()
    {
        var account = NewAccount();
        account.MarkConnected(Now);

        // A healthy connection is not permission to trade real money.
        Assert.True(account.CanTrade);
        Assert.False(account.CanReachExchange);
    }

    [Fact]
    public void AnUnvalidatedAccountCannotBePromoted()
    {
        var account = NewAccount();

        Assert.Throws<InvalidOperationException>(() => account.Promote(TradingStage.Proving, Now));
    }

    [Fact]
    public void AStageCannotBeSkipped()
    {
        var account = NewAccount();
        account.MarkConnected(Now);

        // Paper straight to Live would bypass the entire proving stage.
        Assert.Throws<InvalidOperationException>(() => account.Promote(TradingStage.Live, Now));
        Assert.Equal(TradingStage.Paper, account.Stage);
    }

    [Fact]
    public void PromotionAdvancesOneStageAtATime()
    {
        var account = NewAccount();
        account.MarkConnected(Now);

        account.Promote(TradingStage.Proving, Now);
        Assert.Equal(TradingStage.Proving, account.Stage);
        Assert.True(account.CanReachExchange);

        account.Promote(TradingStage.Live, Now);
        Assert.Equal(TradingStage.Live, account.Stage);
    }

    [Fact]
    public void PromotionRecordsWhenTheStageChanged()
    {
        var account = NewAccount();
        account.MarkConnected(Now);

        account.Promote(TradingStage.Proving, Now);

        Assert.Equal(Now, account.StageChangedAtUtc);
    }

    [Fact]
    public void DisconnectingReturnsTheAccountToPaper()
    {
        var account = NewAccount();
        account.MarkConnected(Now);
        account.Promote(TradingStage.Proving, Now);

        account.MarkDisconnected();

        // Re-connecting a key must not silently restore a previous clearance.
        Assert.Equal(TradingStage.Paper, account.Stage);
        Assert.False(account.CanReachExchange);
    }

    [Fact]
    public void SuspendingReturnsTheAccountToPaper()
    {
        var account = NewAccount();
        account.MarkConnected(Now);
        account.Promote(TradingStage.Proving, Now);

        account.MarkSuspended(Now);

        Assert.Equal(TradingStage.Paper, account.Stage);
        Assert.False(account.CanReachExchange);
    }

    [Fact]
    public void ReturningToPaperIsAlwaysAllowed()
    {
        var account = NewAccount();
        account.MarkConnected(Now);
        account.Promote(TradingStage.Proving, Now);
        account.Promote(TradingStage.Live, Now);

        // A safety action must never be blocked by the state it is correcting.
        account.ReturnToPaper(Now);

        Assert.Equal(TradingStage.Paper, account.Stage);
        Assert.False(account.CanReachExchange);
    }

    [Fact]
    public void PaperIsTheDefaultEnumValueSoAnUnsetStageIsTheSafeOne()
    {
        // If a row were ever written without an explicit stage, the value it
        // falls back to must be the one that cannot reach the exchange.
        Assert.Equal(TradingStage.Paper, default(TradingStage));
        Assert.Equal(0, (int)TradingStage.Paper);
    }

    private static ExchangeAccount NewAccount() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        ExchangeKind.Kraken,
        "Kraken main",
        "exchange-credential/user/account",
        Now);
}
