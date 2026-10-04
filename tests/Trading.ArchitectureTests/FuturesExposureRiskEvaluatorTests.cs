using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class FuturesExposureRiskEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static FuturesExposureRiskEvaluator Policy() =>
        new(2m, 1_000m, 200m, TimeSpan.FromSeconds(30));

    private static FuturesExposureEvidence Evidence() =>
        new(100m, 800m, 2m, 100m, Now, Now, Now, false, "USD", 1m);

    [Fact]
    public void ExactCeilingsAndFreshnessBoundaryPermitOnlyMeasuredExposure()
    {
        var evidence = Evidence() with
        {
            AccountObservedAtUtc = Now.AddSeconds(-30),
            PositionsObservedAtUtc = Now.AddSeconds(-30),
            MarketObservedAtUtc = Now.AddSeconds(-30)
        };
        Assert.True(Policy().EvaluateIncrease(evidence, "usd", 2m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(evidence, "USD", 2.0001m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(evidence with { AvailableMargin = 99.99m },
            "USD", 2m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(evidence with { GrossNotional = 800.01m },
            "USD", 2m, 100m, Now).IsAllowed);
    }

    [Fact]
    public void EveryMissingStaleFutureOrNonUtcObservationBlocksAnIncrease()
    {
        foreach (var evidence in new[]
        {
            Evidence() with { AccountObservedAtUtc = null },
            Evidence() with { AccountObservedAtUtc = Now.AddSeconds(-31) },
            Evidence() with { PositionsObservedAtUtc = Now.AddSeconds(-31) },
            Evidence() with { MarketObservedAtUtc = Now.AddSeconds(-31) },
            Evidence() with { MarketObservedAtUtc = Now.AddSeconds(1) },
            Evidence() with { AccountObservedAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) }
        })
            Assert.False(Policy().EvaluateIncrease(evidence, "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence(), "USD", 1m, 100m,
            Now.ToOffset(TimeSpan.FromHours(1))).IsAllowed);
    }

    [Fact]
    public void LeverageAndUnknownOrderStateCannotIncreaseExposure()
    {
        Assert.False(Policy().EvaluateIncrease(Evidence() with { ObservedLeverage = 2.01m },
            "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { ObservedLeverage = 0m },
            "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { HasUnresolvedOrders = true },
            "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence(), "EUR", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { AvailableMargin = -1m },
            "USD", 1m, 100m, Now).IsAllowed);
    }

    [Fact]
    public void ContractMultiplierControlsNotionalAndMarginWithoutAssumingUnitContracts()
    {
        Assert.True(Policy().EvaluateIncrease(Evidence() with { ContractMultiplier = 0.1m },
            "USD", 20m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { ContractMultiplier = 10m },
            "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with
        {
            ContractMultiplier = 0.5m, GrossNotional = 0m, AvailableMargin = 49.99m
        }, "USD", 2m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { ContractMultiplier = 0m },
            "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { ContractMultiplier = -1m },
            "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { ContractMultiplier = decimal.MaxValue },
            "USD", 2m, 100m, Now).IsAllowed);
    }

    [Fact]
    public void PriceDeviationAndDecimalOverflowFailClosed()
    {
        Assert.True(Policy().EvaluateIncrease(Evidence() with { GrossNotional = 0m },
            "USD", 1m, 102m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with { GrossNotional = 0m },
            "USD", 1m, 102.01m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence() with
        {
            GrossNotional = decimal.MaxValue, AvailableMargin = decimal.MaxValue
        }, "USD", 1m, 100m, Now).IsAllowed);
        Assert.False(Policy().EvaluateIncrease(Evidence(),
            "USD", decimal.MaxValue, decimal.MaxValue, Now).IsAllowed);
    }

    [Fact]
    public void OperatorCannotRelaxMandatoryLeverageAgeOrPriceDeviationCeilings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FuturesExposureRiskEvaluator(2.01m, 1000m, 200m, TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FuturesExposureRiskEvaluator(2m, 1000m, 200m, TimeSpan.FromSeconds(31)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FuturesExposureRiskEvaluator(2m, 1000m, 200m, TimeSpan.FromSeconds(30), 0.021m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FuturesExposureRiskEvaluator(2m, 100m, 200m, TimeSpan.FromSeconds(30)));
    }
}
