using Trading.Domain.Futures;

namespace Trading.ArchitectureTests;

public sealed class FuturesPositionTests
{
    private static readonly DateTimeOffset InitialValuation = DateTimeOffset.UtcNow.AddMinutes(-10);

    [Fact]
    public void CalculatesLongAndShortUnrealizedPnlUsingDecimals()
    {
        var longPosition = Create(FuturesPositionSide.LongPosition, quantity: 1.25m, entryPrice: 100.10m, markPrice: 101.35m);
        var shortPosition = Create(FuturesPositionSide.ShortPosition, quantity: 1.25m, entryPrice: 100.10m, markPrice: 101.35m);

        Assert.Equal(1.5625m, longPosition.UnrealizedPnl);
        Assert.Equal(-1.5625m, shortPosition.UnrealizedPnl);
    }

    [Fact]
    public void VerifiedNonUnitContractMultiplierControlsBothSidesAndPartialRealization()
    {
        var longPosition = Create(FuturesPositionSide.LongPosition, quantity: 3m,
            entryPrice: 100m, markPrice: 104m, contractMultiplier: 0.25m);
        var shortPosition = Create(FuturesPositionSide.ShortPosition, quantity: 3m,
            entryPrice: 100m, markPrice: 104m, contractMultiplier: 0.25m);

        Assert.Equal(3m, longPosition.UnrealizedPnl);
        Assert.Equal(-3m, shortPosition.UnrealizedPnl);

        var reducedLong = longPosition.ApplyReduceOnly(1m, 106m, InitialValuation.AddMinutes(1));
        var reducedShort = shortPosition.ApplyReduceOnly(1m, 106m, InitialValuation.AddMinutes(1));
        Assert.Equal(0.25m, reducedLong.ContractMultiplier);
        Assert.Equal(1.5m, reducedLong.RealizedPnl);
        Assert.Equal(-1.5m, reducedShort.RealizedPnl);
        Assert.Equal(3m, reducedLong.UnrealizedPnl);
        Assert.Equal(-3m, reducedShort.UnrealizedPnl);
    }

    [Fact]
    public void ObservesFundingMarginMarkAndLiquidationWithoutChangingLeverage()
    {
        var position = Create();
        var leverage = position.ObservedLeverage;

        var valued = position.ObserveValuation(110m, 25m, 80m, InitialValuation.AddMinutes(1));
        var funded = valued.AccrueObservedFunding(-0.15m, InitialValuation.AddMinutes(2));

        Assert.Equal(110m, funded.MarkPrice);
        Assert.Equal(25m, funded.Margin);
        Assert.Equal(80m, funded.LiquidationPrice);
        Assert.Equal(-0.15m, funded.FundingAccrued);
        Assert.Equal(leverage, funded.ObservedLeverage);
        Assert.Equal(10m, funded.UnrealizedPnl);
    }

    [Fact]
    public void RejectsFutureMissingAndStaleValuationTimestamps()
    {
        Assert.Throws<ArgumentException>(() => Create(valuedAtUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(valuedAtUtc: DateTimeOffset.UtcNow.AddMinutes(1)));

        var position = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            position.ObserveValuation(101m, 10m, 80m, InitialValuation));
    }

    [Fact]
    public void ReduceOnlyCanDecreaseOrCloseButNeverIncreaseOrFlip()
    {
        var position = Create(quantity: 2m);

        var reduced = position.ApplyReduceOnly(1m, 110m, InitialValuation.AddMinutes(1));
        Assert.Equal(2m, position.Quantity);
        Assert.Equal(1m, reduced.Quantity);
        Assert.Equal(10m, reduced.RealizedPnl);
        Assert.Equal(FuturesPositionStatus.Open, reduced.Status);
        Assert.Throws<InvalidOperationException>(() =>
            reduced.ApplyReduceOnly(1.01m, 110m, InitialValuation.AddMinutes(2)));

        var closed = reduced.ApplyReduceOnly(1m, 110m, InitialValuation.AddMinutes(2));
        Assert.Equal(0m, closed.Quantity);
        Assert.Equal(FuturesPositionStatus.Closed, closed.Status);
        Assert.Throws<InvalidOperationException>(() =>
            closed.AccrueObservedFunding(1m, InitialValuation.AddMinutes(3)));
    }

    [Fact]
    public void LiquidationIsTerminalAndUsesOnlyObservedRealizedPnl()
    {
        var position = Create(markPrice: 90m, liquidationPrice: 85m);

        var liquidated = position.MarkLiquidated(-17.25m, InitialValuation.AddMinutes(1));

        Assert.Equal(FuturesPositionStatus.Liquidated, liquidated.Status);
        Assert.True(liquidated.IsTerminal);
        Assert.Equal(0m, liquidated.Quantity);
        Assert.Equal(90m, liquidated.MarkPrice);
        Assert.Equal(-17.25m, liquidated.RealizedPnl);
        Assert.Equal(0m, liquidated.UnrealizedPnl);
        Assert.Throws<InvalidOperationException>(() =>
            liquidated.ObserveValuation(90m, 1m, 80m, InitialValuation.AddMinutes(2)));
        Assert.Throws<ArgumentException>(() =>
            position.MarkLiquidated(-17.25m, InitialValuation.AddMinutes(1).ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void RejectsNegativeMarginAndNonPositivePrices()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FuturesPosition(
                Guid.NewGuid(),
                "BTC-USD-LINEAR",
                FuturesPositionSide.LongPosition,
                1m,
                contractMultiplier: 1m,
                entryPrice: 0m,
                markPrice: 100m,
                margin: 10m,
                liquidationPrice: 80m,
                fundingAccrued: 0m,
                realizedPnl: 0m,
                observedLeverage: 2m,
                marginMode: FuturesMarginMode.Isolated,
                valuedAtUtc: InitialValuation));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create().ObserveValuation(100m, -1m, 80m, InitialValuation.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(contractMultiplier: 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(contractMultiplier: -1m));
    }

    private static FuturesPosition Create(
        FuturesPositionSide side = FuturesPositionSide.LongPosition,
        decimal quantity = 1m,
        decimal entryPrice = 100m,
        decimal markPrice = 100m,
        decimal liquidationPrice = 80m,
        decimal contractMultiplier = 1m,
        DateTimeOffset? valuedAtUtc = null) =>
        new(
            Guid.NewGuid(),
            "BTC-USD-LINEAR",
            side,
            quantity,
            contractMultiplier,
            entryPrice,
            markPrice,
            margin: 10m,
            liquidationPrice: liquidationPrice,
            fundingAccrued: 0m,
            realizedPnl: 0m,
            observedLeverage: 2m,
            marginMode: FuturesMarginMode.Isolated,
            valuedAtUtc: valuedAtUtc ?? InitialValuation);
}
