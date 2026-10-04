using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingHistoricalQualificationTests
{
    [Fact]
    public async Task SingleTimeframeQualificationFailsClosedForExactMultiRoleStrategies()
    {
        var toUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero);
        var fromUtc = toUtc.AddHours(-40);
        var candles = Enumerable.Range(0, 40)
            .Select(index =>
            {
                var open = 100m * (1m + (index * 0.02m));
                return new Candle(
                    "XRP/EUR",
                    CandleInterval.OneHour,
                    fromUtc.AddHours(index),
                    fromUtc.AddHours(index + 1),
                    open,
                    open * 1.03m,
                    open * 0.99m,
                    open * 1.02m,
                    1_000m,
                    true,
                    false);
            })
            .ToArray();
        var service = new PaperTrainingHistoricalQualification(
            new SuppliedHistoricalSource(candles),
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
        var slot = PaperTrainingActivationService.ApprovedSlots[0];

        var results = await service.RunAsync(new(
            [slot],
            CandleInterval.OneHour,
            fromUtc,
            toUtc,
            PaperTrainingQualificationGate.PlatformDefault));

        var result = Assert.Single(results);
        Assert.False(result.Accepted);
        Assert.Equal(0, result.CompletedTrades);
        Assert.Contains("single-interval replay", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, result.DatasetFingerprint.Length);
    }

    [Fact]
    public async Task RefusesUnsafeOrInsufficientHistoricalEvidence()
    {
        var toUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero);
        var fromUtc = toUtc.AddHours(-2);
        var source = new SuppliedHistoricalSource(
        [
            new Candle("XRP/EUR", CandleInterval.OneHour, fromUtc, fromUtc.AddHours(1),
                100m, 101m, 99m, 100m, 1m, true, false)
        ]);
        var service = new PaperTrainingHistoricalQualification(
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault());

        var result = Assert.Single(await service.RunAsync(new(
            [PaperTrainingActivationService.ApprovedSlots[0]],
            CandleInterval.OneHour,
            fromUtc,
            toUtc,
            PaperTrainingQualificationGate.PlatformDefault)));

        Assert.False(result.Accepted);
        Assert.Contains("30", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadsHistoricalCandlesOnceForMultipleStrategiesOnTheSameSymbol()
    {
        var toUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero);
        var fromUtc = toUtc.AddHours(-40);
        var source = new CountingHistoricalSource(Enumerable.Range(0, 40)
            .Select(index => new Candle(
                "XRP/EUR",
                CandleInterval.OneHour,
                fromUtc.AddHours(index),
                fromUtc.AddHours(index + 1),
                100m + index,
                101m + index,
                99m + index,
                100.5m + index,
                1_000m,
                true,
                false))
            .ToArray());
        var service = new PaperTrainingHistoricalQualification(
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
        var slots = PaperTrainingActivationService.ApprovedSlots
            .Where(slot => slot.Symbol == "XRP/EUR")
            .ToArray();

        var results = await service.RunAsync(new(
            slots,
            CandleInterval.OneHour,
            fromUtc,
            toUtc,
            PaperTrainingQualificationGate.PlatformDefault));

        Assert.Equal(slots.Length, results.Count);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task RejectsCandlesThatDoNotMatchTheCandidateInterval()
    {
        var toUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-2), TimeSpan.Zero);
        var fromUtc = toUtc.AddHours(-30);
        var candles = Enumerable.Range(0, 30)
            .Select(index => new Candle(
                "XRP/EUR",
                CandleInterval.OneHour,
                fromUtc.AddHours(index),
                fromUtc.AddHours(index + 1),
                100m,
                101m,
                99m,
                100m,
                1_000m,
                true,
                false))
            .ToArray();
        var service = new PaperTrainingHistoricalQualification(
            new SuppliedHistoricalSource(candles),
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Interval = CandleInterval.FifteenMinutes
        };

        var result = Assert.Single(await service.RunAsync(new(
            [slot],
            CandleInterval.FifteenMinutes,
            fromUtc,
            toUtc,
            PaperTrainingQualificationGate.PlatformDefault)));

        Assert.False(result.Accepted);
        Assert.Contains("wrong symbol, interval, duration", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("unsafe")]
    [InlineData("zero price")]
    [InlineData("unclosed at cutoff")]
    public async Task RejectsBadCandlesInsideTheRangeWithoutFilteringThemOut(string defect)
    {
        var fromUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-7), TimeSpan.Zero);
        var candles = ValidCandles(fromUtc);
        var affectedIndex = defect == "unclosed at cutoff" ? candles.Length - 1 : 12;
        var original = candles[affectedIndex];
        var previousFingerprint = Trading.Backtesting.HistoricalCandleFingerprint.Compute(candles);
        var replacement = new Candle(original.Symbol, original.Interval,
            original.OpenTimeUtc,
            defect == "unclosed at cutoff" ? original.CloseTimeUtc.AddHours(1) : original.CloseTimeUtc,
            defect == "zero price" ? 0m : original.Open,
            original.High, original.Low, original.Close, original.Volume,
            defect != "incomplete", false,
            defect == "unsafe" ? [DataQualityIssue.Stale] : null);
        candles[affectedIndex] = replacement;
        var service = new PaperTrainingHistoricalQualification(
            new SuppliedHistoricalSource(candles),
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault());

        var result = Assert.Single(await service.RunAsync(new(
            [PaperTrainingActivationService.ApprovedSlots[0]],
            CandleInterval.OneHour,
            fromUtc,
            fromUtc.AddHours(candles.Length),
            PaperTrainingQualificationGate.PlatformDefault)));

        Assert.False(result.Accepted);
        Assert.Equal(0, result.CompletedTrades);
        Assert.Contains("unsafe value", result.Reason, StringComparison.Ordinal);
        Assert.NotEqual(previousFingerprint, result.DatasetFingerprint);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("out of order")]
    public async Task RejectsChronologyErrorsInsteadOfSortingThemIntoValidEvidence(string defect)
    {
        var fromUtc = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-7), TimeSpan.Zero);
        var candles = ValidCandles(fromUtc);
        if (defect == "duplicate")
            candles[12] = candles[11];
        else
            (candles[12], candles[13]) = (candles[13], candles[12]);
        var service = new PaperTrainingHistoricalQualification(
            new SuppliedHistoricalSource(candles),
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault());

        var result = Assert.Single(await service.RunAsync(new(
            [PaperTrainingActivationService.ApprovedSlots[0]],
            CandleInterval.OneHour,
            fromUtc,
            fromUtc.AddHours(candles.Length),
            PaperTrainingQualificationGate.PlatformDefault)));

        Assert.False(result.Accepted);
        Assert.Contains("out-of-order", result.Reason, StringComparison.Ordinal);
    }

    private static Candle[] ValidCandles(DateTimeOffset fromUtc) =>
        Enumerable.Range(0, 40)
            .Select(index => new Candle(
                "XRP/EUR", CandleInterval.OneHour,
                fromUtc.AddHours(index), fromUtc.AddHours(index + 1),
                100m, 101m, 99m, 100m, 1_000m, true, false))
            .ToArray();

    private sealed class SuppliedHistoricalSource(IReadOnlyList<Candle> candles) : IHistoricalCandleSource
    {
        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(candles);
        }
    }

    private sealed class CountingHistoricalSource(IReadOnlyList<Candle> candles) : IHistoricalCandleSource
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(candles);
        }
    }
}
