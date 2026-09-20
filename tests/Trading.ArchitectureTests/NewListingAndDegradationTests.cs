using Trading.Domain.Universe;

namespace Trading.ArchitectureTests;

public sealed class NewListingAndDegradationTests
{
    private static readonly string[] SpotPermission = { "SPOT" };
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    // ---- New listing policy (task 3B.7) ----

    [Fact]
    public void UnknownListingAgeIsNeverTreatedAsMature()
    {
        var policy = NewListingPolicy.PlatformFloor;

        Assert.Equal(NewListingRestriction.Unknown, policy.Restriction(null));
        Assert.False(policy.Permits(EligibilityPurpose.Research, null));
        Assert.False(policy.Permits(EligibilityPurpose.Paper, null));
        Assert.False(policy.Permits(EligibilityPurpose.SpotLive, null));
    }

    [Theory]
    [InlineData(1, NewListingRestriction.TrackingOnly)]
    [InlineData(10, NewListingRestriction.ResearchOnly)]
    [InlineData(45, NewListingRestriction.SimulationOnly)]
    [InlineData(200, NewListingRestriction.Unrestricted)]
    public void ListingAgeMapsToTheExpectedRestriction(int days, NewListingRestriction expected) =>
        Assert.Equal(expected, NewListingPolicy.PlatformFloor.Restriction(TimeSpan.FromDays(days)));

    [Fact]
    public void ABrandNewListingPermitsNothingBeyondTracking()
    {
        var policy = NewListingPolicy.PlatformFloor;
        var age = TimeSpan.FromDays(2);

        Assert.False(policy.Permits(EligibilityPurpose.Research, age));
        Assert.False(policy.Permits(EligibilityPurpose.Backtest, age));
        Assert.False(policy.Permits(EligibilityPurpose.Paper, age));
        Assert.False(policy.Permits(EligibilityPurpose.SpotLive, age));
    }

    [Fact]
    public void SimulationOnlyWindowNeverPermitsLiveTrading()
    {
        var policy = NewListingPolicy.PlatformFloor;
        var age = TimeSpan.FromDays(45);

        Assert.True(policy.Permits(EligibilityPurpose.Paper, age));
        Assert.False(policy.Permits(EligibilityPurpose.SpotTest, age));
        Assert.False(policy.Permits(EligibilityPurpose.SpotLive, age));
        Assert.False(policy.Permits(EligibilityPurpose.FuturesLive, age));
    }

    [Fact]
    public void ConfigurationCanOnlyLengthenTheWindows()
    {
        var permissive = new NewListingPolicy(
            TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(3));

        var effective = permissive.Stricter(NewListingPolicy.PlatformFloor);

        Assert.Equal(NewListingPolicy.PlatformFloor.ResearchAfter, effective.ResearchAfter);
        Assert.Equal(NewListingPolicy.PlatformFloor.SimulationAfter, effective.SimulationAfter);
        Assert.Equal(NewListingPolicy.PlatformFloor.UnrestrictedAfter, effective.UnrestrictedAfter);
    }

    [Fact]
    public void ConfigurationMayDelayAccessFurther()
    {
        var strict = new NewListingPolicy(
            TimeSpan.FromDays(30), TimeSpan.FromDays(120), TimeSpan.FromDays(365));

        var effective = strict.Stricter(NewListingPolicy.PlatformFloor);

        Assert.Equal(TimeSpan.FromDays(365), effective.UnrestrictedAfter);
    }

    [Fact]
    public void WindowsMustNotDecrease() =>
        Assert.Throws<ArgumentException>(() => new NewListingPolicy(
            TimeSpan.FromDays(30), TimeSpan.FromDays(10), TimeSpan.FromDays(90)));

    [Fact]
    public void NonePurposeIsNeverPermitted() =>
        Assert.False(NewListingPolicy.PlatformFloor.Permits(
            EligibilityPurpose.None, TimeSpan.FromDays(1000)));

    [Fact]
    public void ExplanationNamesTheRestriction()
    {
        var explanation = NewListingPolicy.PlatformFloor.Explain(
            EligibilityPurpose.SpotLive, TimeSpan.FromDays(45));

        Assert.Contains("blocked", explanation, StringComparison.Ordinal);
        Assert.Contains(nameof(NewListingRestriction.SimulationOnly), explanation, StringComparison.Ordinal);
    }

    // ---- Degradation policy (task 3B.8) ----

    [Fact]
    public void AHealthyInstrumentPermitsIncreases()
    {
        var decision = Policy().Evaluate(Healthy(), HealthyMetrics(), Now);

        Assert.Equal(ExposureDirective.AllowIncrease, decision.Directive);
        Assert.True(decision.PermitsNewExposure);
    }

    [Fact]
    public void ReductionIsPermittedUnderEveryDirective()
    {
        // Blocking exits would trap capital in a deteriorating market.
        Assert.True(ExposureDecision.PermitsReduction);

        foreach (var directive in Enum.GetValues<ExposureDirective>())
        {
            Assert.True(ExposureDecision.PermitsReduction, directive.ToString());
        }
    }

    [Fact]
    public void MissingMeasurementsBlockNewExposure()
    {
        var decision = Policy().Evaluate(Healthy(), null, Now);

        Assert.Equal(ExposureDirective.ReduceOnly, decision.Directive);
        Assert.False(decision.PermitsNewExposure);
        Assert.True(decision.PermitsPartialReduction);
    }

    [Fact]
    public void StaleMeasurementsBlockNewExposure()
    {
        var stale = HealthyMetrics(computedAtUtc: Now.AddHours(-48));

        var decision = Policy().Evaluate(Healthy(), stale, Now);

        Assert.Equal(ExposureDirective.ReduceOnly, decision.Directive);
        Assert.Contains("stale", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DataGapsBlockNewExposure()
    {
        var gappy = HealthyMetrics(dataGapCount: 3);

        Assert.Equal(ExposureDirective.ReduceOnly, Policy().Evaluate(Healthy(), gappy, Now).Directive);
    }

    [Fact]
    public void SuspendedInstrumentIsReduceOnly()
    {
        var instrument = Healthy();
        instrument.Suspend("Operator action.", Now);

        var decision = Policy().Evaluate(instrument, HealthyMetrics(), Now);

        Assert.Equal(ExposureDirective.ReduceOnly, decision.Directive);
        Assert.True(decision.PermitsPartialReduction);
    }

    [Fact]
    public void MissingTradingRulesForceCloseOnly()
    {
        // Without filters an order cannot be correctly sized, so a partial
        // reduction could be rounded into a materially different order.
        var instrument = Healthy();
        instrument.ClearFilters();

        var decision = Policy().Evaluate(instrument, HealthyMetrics(), Now);

        Assert.Equal(ExposureDirective.CloseOnly, decision.Directive);
        Assert.False(decision.PermitsPartialReduction);
        Assert.True(ExposureDecision.PermitsReduction);
    }

    [Fact]
    public void NonTradingExchangeStatusForcesCloseOnly()
    {
        var instrument = Healthy();
        instrument.ObserveInCatalogue(InstrumentTradingStatus.Halted, "MAINTENANCE", SpotPermission, Now);

        Assert.Equal(ExposureDirective.CloseOnly, Policy().Evaluate(instrument, HealthyMetrics(), Now).Directive);
    }

    [Fact]
    public void RemovedInstrumentForcesCloseOnly()
    {
        var instrument = Healthy();
        instrument.Remove("Delisted.", Now);

        Assert.Equal(ExposureDirective.CloseOnly, Policy().Evaluate(instrument, HealthyMetrics(), Now).Directive);
    }

    [Fact]
    public void AbsentFromCatalogueForcesCloseOnly()
    {
        var instrument = Healthy();
        instrument.MarkAbsentFromCatalogue(Now, "Not in a complete catalogue.");

        Assert.Equal(ExposureDirective.CloseOnly, Policy().Evaluate(instrument, HealthyMetrics(), Now).Directive);
    }

    [Fact]
    public void EveryDecisionStatesItsReason()
    {
        var instrument = Healthy();
        instrument.ClearFilters();

        Assert.False(string.IsNullOrWhiteSpace(Policy().Evaluate(instrument, null, Now).Reason));
    }

    [Fact]
    public void PolicyRejectsANonPositiveEvidenceAge() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstrumentDegradationPolicy(TimeSpan.Zero));

    private static InstrumentDegradationPolicy Policy() => new(TimeSpan.FromHours(6));

    private static Instrument Healthy()
    {
        var instrument = Instrument.CreateSeed(
            Guid.NewGuid(), "Kraken", "BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);

        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotPermission, Now.AddMinutes(-5));
        instrument.MarkFiltersLoaded(Now.AddMinutes(-5));
        instrument.RecordFirstObservedCandle(Now.AddDays(-400));
        return instrument;
    }

    private static InstrumentMetrics HealthyMetrics(
        DateTimeOffset? computedAtUtc = null,
        int dataGapCount = 0)
    {
        var computed = computedAtUtc ?? Now.AddMinutes(-5);

        return new InstrumentMetrics(
            instrumentId: Guid.NewGuid(),
            windowStartUtc: computed.AddDays(-30),
            windowEndUtc: computed.AddMinutes(-5),
            computedAtUtc: computed,
            rollingQuoteVolume: 50_000_000m,
            medianQuoteVolume: 40_000_000m,
            minimumQuoteVolume: 20_000_000m,
            averageSpread: 0.0004m,
            worstSpread: 0.0009m,
            estimatedSlippage: 0.0010m,
            tradeFrequency: 900m,
            dataGapCount: dataGapCount,
            staleEventCount: 0);
    }
}
