using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingHistoricalQualificationTests
{
    [Fact]
    public async Task UsesApprovedLiveEvaluatorAndAppliesAllHistoricalGates()
    {
        var toUtc = DateTimeOffset.UtcNow.AddHours(-1);
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
        Assert.True(result.Accepted, result.Reason);
        Assert.True(result.CompletedTrades >= 3);
        Assert.True(result.NetReturnPercent >= 0m);
        Assert.True(result.MaximumDrawdownPercent <= 20m);
        Assert.Equal(64, result.DatasetFingerprint.Length);
    }

    [Fact]
    public async Task RefusesUnsafeOrInsufficientHistoricalEvidence()
    {
        var toUtc = DateTimeOffset.UtcNow.AddHours(-1);
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
        var toUtc = DateTimeOffset.UtcNow.AddHours(-1);
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
        var toUtc = DateTimeOffset.UtcNow.AddHours(-1);
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
