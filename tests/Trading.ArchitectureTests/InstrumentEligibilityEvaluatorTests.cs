using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Xunit;

namespace Trading.ArchitectureTests;

public sealed class InstrumentEligibilityEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] UsdtOnly = { "USDT" };
    private static readonly string[] SpotPermission = { "SPOT" };

    private static readonly decimal[] OddSample = { 5m, 1m, 3m };

    private static readonly decimal[] EvenSample = { 4m, 2m, 1m, 5m };

    private static readonly EligibilityScope FourHourSpotBacktest =
        new(EligibilityPurpose.Backtest, CandleInterval.FourHours, TradingProductType.Spot);

    private static EligibilityThresholds Floor() => new(
        minimumRollingQuoteVolume: 1_000_000m,
        minimumMedianQuoteVolume: 500_000m,
        maximumSpread: 0.0020m,
        maximumEstimatedSlippage: 0.0050m,
        minimumHistoryCandles: 500,
        minimumListingAge: TimeSpan.FromDays(90),
        maximumEvidenceAge: TimeSpan.FromHours(24));

    private static InstrumentEligibilityEvaluator Evaluator() => new(UsdtOnly, Floor());

    private static Instrument HealthyInstrument()
    {
        var instrument = Instrument.CreateSeed(
            Guid.NewGuid(), "Binance", "BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);
        instrument.ObserveInCatalogue("TRADING", SpotPermission, Now, Now.AddYears(-2));
        instrument.MarkFiltersLoaded(Now);
        return instrument;
    }

    private static InstrumentMetrics HealthyMetrics(
        Guid instrumentId,
        decimal rollingVolume = 10_000_000m,
        decimal medianVolume = 5_000_000m,
        decimal averageSpread = 0.0005m,
        decimal slippage = 0.0010m,
        int gaps = 0,
        int staleEvents = 0,
        DateTimeOffset? computedAtUtc = null) =>
        new(
            instrumentId,
            Now.AddDays(-7),
            Now,
            computedAtUtc ?? Now,
            rollingVolume,
            medianVolume,
            minimumQuoteVolume: 1_000_000m,
            averageSpread,
            worstSpread: averageSpread * 3m,
            estimatedSlippage: slippage,
            tradeFrequency: 1200m,
            dataGapCount: gaps,
            staleEventCount: staleEvents);

    private static EligibilityInputs ApprovedInputs(int history = 5_000) =>
        new(history, StrategyApproved: true, "Approved for this strategy, timeframe and mode.");

    private static EligibilityEvaluation EvaluateWith(
        Instrument? instrument = null,
        InstrumentMetrics? metrics = null,
        EligibilityInputs? inputs = null,
        EligibilityThresholds? configured = null,
        DateTimeOffset? nowUtc = null,
        bool omitMetrics = false)
    {
        var subject = instrument ?? HealthyInstrument();
        return Evaluator().Evaluate(
            subject,
            FourHourSpotBacktest,
            omitMetrics ? null : metrics ?? HealthyMetrics(subject.Id),
            configured ?? Floor(),
            inputs ?? ApprovedInputs(),
            nowUtc ?? Now);
    }

    [Fact]
    public void AFullyHealthyInstrumentPassesEveryGate()
    {
        var evaluation = EvaluateWith();

        Assert.True(evaluation.Passed);
        Assert.Equal(12, evaluation.GateResults.Count);
        Assert.Empty(evaluation.FailedGates);
        Assert.Contains("all 12 gates passed", evaluation.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMetricsFailClosedRatherThanBeingAssumedFine()
    {
        var evaluation = EvaluateWith(omitMetrics: true);

        Assert.False(evaluation.Passed);
        var failedGates = evaluation.FailedGates.Select(result => result.Gate).ToList();
        Assert.Contains(EligibilityGate.DataHealth, failedGates);
        Assert.Contains(EligibilityGate.Liquidity, failedGates);
        Assert.Contains(EligibilityGate.Spread, failedGates);
        Assert.Contains(EligibilityGate.Slippage, failedGates);
    }

    [Fact]
    public void ASingleVolumeSpikeWithAFailingMedianDoesNotPassTheLiquidityGate()
    {
        var instrument = HealthyInstrument();
        var spiky = HealthyMetrics(instrument.Id, rollingVolume: 50_000_000m, medianVolume: 1_000m);

        var evaluation = EvaluateWith(instrument, spiky);

        var liquidity = Assert.Single(evaluation.GateResults, result => result.Gate == EligibilityGate.Liquidity);
        Assert.False(liquidity.Passed);
        Assert.Contains("spike", liquidity.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnloadedFiltersBlockEligibility()
    {
        var instrument = HealthyInstrument();
        instrument.ClearFilters();

        var evaluation = EvaluateWith(instrument);

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.FailedGates, result => result.Gate == EligibilityGate.FiltersLoaded);
    }

    [Fact]
    public void StaleEvidenceFailsTheDataHealthGateEvenWhenTheNumbersLookGood()
    {
        var instrument = HealthyInstrument();
        var metrics = HealthyMetrics(instrument.Id);

        var evaluation = EvaluateWith(instrument, metrics, nowUtc: Now.AddHours(25));

        var dataHealth = Assert.Single(evaluation.GateResults, result => result.Gate == EligibilityGate.DataHealth);
        Assert.False(dataHealth.Passed);
        Assert.Contains("older than the maximum evidence age", dataHealth.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADataGapInsideTheWindowBlocksEligibility()
    {
        var instrument = HealthyInstrument();
        var metrics = HealthyMetrics(instrument.Id, gaps: 2);

        var evaluation = EvaluateWith(instrument, metrics);

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.FailedGates, result => result.Gate == EligibilityGate.DataHealth);
    }

    [Fact]
    public void AStaleDataEventBlocksEligibility()
    {
        var instrument = HealthyInstrument();
        var metrics = HealthyMetrics(instrument.Id, staleEvents: 1);

        var evaluation = EvaluateWith(instrument, metrics);

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.FailedGates, result => result.Gate == EligibilityGate.DataHealth);
    }

    [Fact]
    public void IncompleteHistoryBlocksEligibilityAndReportsBothValues()
    {
        var evaluation = EvaluateWith(inputs: ApprovedInputs(history: 100));

        var history = Assert.Single(evaluation.GateResults, result => result.Gate == EligibilityGate.HistoryComplete);
        Assert.False(history.Passed);
        Assert.Equal(100m, history.MeasuredValue);
        Assert.Equal(500m, history.Threshold);
    }

    [Fact]
    public void SpreadAboveTheMaximumBlocksEligibility()
    {
        var instrument = HealthyInstrument();
        var metrics = HealthyMetrics(instrument.Id, averageSpread: 0.0100m);

        var evaluation = EvaluateWith(instrument, metrics);

        Assert.Contains(evaluation.FailedGates, result => result.Gate == EligibilityGate.Spread);
    }

    [Fact]
    public void SlippageAboveTheMaximumBlocksEligibility()
    {
        var instrument = HealthyInstrument();
        var metrics = HealthyMetrics(instrument.Id, slippage: 0.0500m);

        var evaluation = EvaluateWith(instrument, metrics);

        Assert.Contains(evaluation.FailedGates, result => result.Gate == EligibilityGate.Slippage);
    }

    [Fact]
    public void ANewlyListedInstrumentFailsTheListingAgeGate()
    {
        var instrument = Instrument.CreateSeed(
            Guid.NewGuid(), "Binance", "PROVEUSDT", "PROVE", "USDT", AssetClass.Cryptocurrency);
        instrument.ObserveInCatalogue("TRADING", SpotPermission, Now, Now.AddDays(-5));
        instrument.MarkFiltersLoaded(Now);

        var evaluation = EvaluateWith(instrument);

        var listingAge = Assert.Single(evaluation.GateResults, result => result.Gate == EligibilityGate.ListingAge);
        Assert.False(listingAge.Passed);
    }

    [Fact]
    public void UnknownListingAgeFailsClosed()
    {
        var instrument = Instrument.CreateSeed(
            Guid.NewGuid(), "Binance", "NEWUSDT", "NEW", "USDT", AssetClass.Cryptocurrency);
        instrument.ObserveInCatalogue("TRADING", SpotPermission, Now);
        instrument.MarkFiltersLoaded(Now);

        var evaluation = EvaluateWith(instrument);

        var listingAge = Assert.Single(evaluation.GateResults, result => result.Gate == EligibilityGate.ListingAge);
        Assert.False(listingAge.Passed);
        Assert.Contains("unknown", listingAge.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingStrategyApprovalBlocksEligibility()
    {
        var evaluation = EvaluateWith(
            inputs: new EligibilityInputs(5_000, StrategyApproved: false, "Not approved for the 1-minute timeframe."));

        var approval = Assert.Single(
            evaluation.GateResults, result => result.Gate == EligibilityGate.StrategyApproval);
        Assert.False(approval.Passed);
        Assert.Contains("1-minute", approval.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorConfigurationCannotBeMorePermissiveThanThePlatformFloor()
    {
        var permissive = new EligibilityThresholds(
            minimumRollingQuoteVolume: 1m,
            minimumMedianQuoteVolume: 1m,
            maximumSpread: 1m,
            maximumEstimatedSlippage: 1m,
            minimumHistoryCandles: 1,
            minimumListingAge: TimeSpan.Zero,
            maximumEvidenceAge: TimeSpan.FromDays(3650));

        var instrument = HealthyInstrument();
        var thin = HealthyMetrics(instrument.Id, rollingVolume: 10m, medianVolume: 10m);

        var evaluation = EvaluateWith(instrument, thin, configured: permissive);

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.FailedGates, result => result.Gate == EligibilityGate.Liquidity);
    }

    [Fact]
    public void OperatorConfigurationMayBeStricterThanThePlatformFloor()
    {
        var strict = new EligibilityThresholds(
            minimumRollingQuoteVolume: 100_000_000m,
            minimumMedianQuoteVolume: 100_000_000m,
            maximumSpread: 0.0001m,
            maximumEstimatedSlippage: 0.0001m,
            minimumHistoryCandles: 100_000,
            minimumListingAge: TimeSpan.FromDays(3650),
            maximumEvidenceAge: TimeSpan.FromMinutes(1));

        var evaluation = EvaluateWith(configured: strict);

        Assert.False(evaluation.Passed);
    }

    [Fact]
    public void ConstrainedByTakesTheStricterValueForEveryField()
    {
        var floor = Floor();
        var permissive = new EligibilityThresholds(1m, 1m, 1m, 1m, 1, TimeSpan.Zero, TimeSpan.FromDays(365));

        var combined = permissive.ConstrainedBy(floor);

        Assert.Equal(floor.MinimumRollingQuoteVolume, combined.MinimumRollingQuoteVolume);
        Assert.Equal(floor.MinimumMedianQuoteVolume, combined.MinimumMedianQuoteVolume);
        Assert.Equal(floor.MaximumSpread, combined.MaximumSpread);
        Assert.Equal(floor.MaximumEstimatedSlippage, combined.MaximumEstimatedSlippage);
        Assert.Equal(floor.MinimumHistoryCandles, combined.MinimumHistoryCandles);
        Assert.Equal(floor.MinimumListingAge, combined.MinimumListingAge);
        Assert.Equal(floor.MaximumEvidenceAge, combined.MaximumEvidenceAge);
    }

    [Fact]
    public void AnExcludedAssetClassFailsEvenWhenEveryOtherGatePasses()
    {
        var instrument = Instrument.CreateSeed(
            Guid.NewGuid(), "Binance", "USDCUSDT", "USDC", "USDT", AssetClass.Stablecoin);
        instrument.ObserveInCatalogue("TRADING", SpotPermission, Now, Now.AddYears(-3));
        instrument.MarkFiltersLoaded(Now);

        var evaluation = EvaluateWith(instrument);

        Assert.Contains(evaluation.FailedGates, result => result.Gate == EligibilityGate.AssetClass);
    }

    [Fact]
    public void EvaluationIsDeterministicForIdenticalInputs()
    {
        var instrument = HealthyInstrument();
        var metrics = HealthyMetrics(instrument.Id, averageSpread: 0.0100m);

        var first = EvaluateWith(instrument, metrics);
        var second = EvaluateWith(instrument, metrics);

        Assert.Equal(first.Passed, second.Passed);
        Assert.Equal(first.Explain(), second.Explain());
        Assert.Equal(
            first.GateResults.Select(result => (result.Gate, result.Passed, result.Detail)),
            second.GateResults.Select(result => (result.Gate, result.Passed, result.Detail)));
    }

    [Fact]
    public void ExplanationNamesEveryFailingGate()
    {
        var instrument = HealthyInstrument();
        instrument.ClearFilters();
        var metrics = HealthyMetrics(instrument.Id, averageSpread: 0.0100m);

        var explanation = EvaluateWith(instrument, metrics).Explain();

        Assert.Contains("FiltersLoaded", explanation, StringComparison.Ordinal);
        Assert.Contains("Spread", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void AScopeWithoutAConcreteIntervalIsRejected()
    {
        var instrument = HealthyInstrument();

        Assert.Throws<ArgumentException>(() => Evaluator().Evaluate(
            instrument,
            new EligibilityScope(EligibilityPurpose.Backtest, CandleInterval.None, TradingProductType.Spot),
            HealthyMetrics(instrument.Id),
            Floor(),
            ApprovedInputs(),
            Now));
    }

    // --- Metrics ------------------------------------------------------------

    [Fact]
    public void MedianOfAnOddSampleIsTheMiddleValue()
    {
        Assert.Equal(3m, InstrumentMetrics.Median(OddSample));
    }

    [Fact]
    public void MedianOfAnEvenSampleAveragesTheTwoMiddleValues()
    {
        Assert.Equal(3m, InstrumentMetrics.Median(EvenSample));
    }

    [Fact]
    public void MedianIsResistantToASingleSpike()
    {
        var withSpike = new[] { 10m, 10m, 10m, 10m, 100_000m };

        Assert.Equal(10m, InstrumentMetrics.Median(withSpike));
    }

    [Fact]
    public void MedianRequiresAtLeastOneObservation()
    {
        Assert.Throws<ArgumentException>(() => InstrumentMetrics.Median(Array.Empty<decimal>()));
    }

    [Fact]
    public void MetricsRejectAnInvertedWindow()
    {
        Assert.Throws<ArgumentException>(() => new InstrumentMetrics(
            Guid.NewGuid(), Now, Now.AddDays(-1), Now, 1m, 1m, 1m, 1m, 1m, 1m, 1m, 0, 0));
    }

    [Fact]
    public void MetricsCannotBeComputedBeforeTheWindowTheyDescribeEnds()
    {
        Assert.Throws<ArgumentException>(() => new InstrumentMetrics(
            Guid.NewGuid(), Now.AddDays(-7), Now, Now.AddDays(-1), 1m, 1m, 1m, 1m, 1m, 1m, 1m, 0, 0));
    }

    [Fact]
    public void MetricsRejectAWorstSpreadTighterThanTheAverage()
    {
        Assert.Throws<ArgumentException>(() => new InstrumentMetrics(
            Guid.NewGuid(), Now.AddDays(-7), Now, Now, 1m, 1m, 1m,
            averageSpread: 0.01m, worstSpread: 0.001m,
            estimatedSlippage: 1m, tradeFrequency: 1m, dataGapCount: 0, staleEventCount: 0));
    }

    [Fact]
    public void MetricsRejectNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstrumentMetrics(
            Guid.NewGuid(), Now.AddDays(-7), Now, Now, -1m, 1m, 1m, 1m, 1m, 1m, 1m, 0, 0));
    }

    [Fact]
    public void UnmeasuredDepthIsNullRatherThanZero()
    {
        var metrics = HealthyMetrics(Guid.NewGuid());

        Assert.Null(metrics.PriceDepth);
    }
}
