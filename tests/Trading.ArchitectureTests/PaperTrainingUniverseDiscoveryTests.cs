using System.Globalization;
using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingUniverseDiscoveryTests
{
    [Fact]
    public async Task SelectsOnlyActiveLiquidEurSpotPairsAndRanksByMedianVolume()
    {
        var asOfUtc = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var pairs = new SuppliedPairSource(
        [
            Pair("BTC/EUR"),
            Pair("ETH/EUR"),
            Pair("DOGE/EUR"),
            Pair("XRP/USD"),
            Pair("USD/CAD"),
            Pair("SOL/EUR", active: false)
        ]);
        var candles = new SymbolCandleSource(new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["BTC/EUR"] = Daily("BTC/EUR", asOfUtc, 30, 50_000m, 100m),
            ["ETH/EUR"] = Daily("ETH/EUR", asOfUtc, 30, 4_000m, 2_000m),
            ["DOGE/EUR"] = Daily("DOGE/EUR", asOfUtc, 30, 0.10m, 100_000m)
        });
        var discovery = new PaperTrainingUniverseDiscovery(
            pairs,
            candles,
            PaperTrainingUniversePolicy.PlatformDefault);

        var result = await discovery.DiscoverAsync(asOfUtc);

        Assert.Collection(
            result,
            candidate =>
            {
                Assert.Equal("ETH/EUR", candidate.Symbol);
                Assert.Equal(8_000_000m, candidate.MedianDailyQuoteVolume);
                Assert.Equal(30, candidate.ObservedDays);
            },
            candidate =>
            {
                Assert.Equal("BTC/EUR", candidate.Symbol);
                Assert.Equal(5_000_000m, candidate.MedianDailyQuoteVolume);
            });
        Assert.Equal(3, candles.CallCount);
    }

    [Fact]
    public async Task RejectsIncompleteLiquidityWindowsAndEnforcesPlatformFloor()
    {
        var asOfUtc = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var candles = new SymbolCandleSource(new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["BTC/EUR"] = Daily("BTC/EUR", asOfUtc, 29, 50_000m, 100m)
        });
        var discovery = new PaperTrainingUniverseDiscovery(
            new SuppliedPairSource([Pair("BTC/EUR")]),
            candles,
            PaperTrainingUniversePolicy.PlatformDefault);

        Assert.Empty(await discovery.DiscoverAsync(asOfUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (PaperTrainingUniversePolicy.PlatformDefault with
            {
                MinimumMedianDailyQuoteVolume = 999_999m
            }).Validate());
    }

    [Fact]
    public async Task PlatformUniverseAdvancesTheFortyMostLiquidEligiblePairs()
    {
        var asOfUtc = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var pairs = Enumerable.Range(1, 45)
            .Select(index => Pair($"TOKEN{index:D2}/EUR"))
            .ToArray();
        var candles = pairs.ToDictionary(
            pair => pair.DisplayName,
            pair =>
            {
                var rank = int.Parse(pair.BaseAsset["TOKEN".Length..], CultureInfo.InvariantCulture);
                return (IReadOnlyList<Candle>)Daily(
                    pair.DisplayName,
                    asOfUtc,
                    30,
                    1m,
                    1_000_000m + rank);
            },
            StringComparer.OrdinalIgnoreCase);
        var discovery = new PaperTrainingUniverseDiscovery(
            new SuppliedPairSource(pairs),
            new SymbolCandleSource(candles),
            PaperTrainingUniversePolicy.PlatformDefault);

        var result = await discovery.DiscoverAsync(asOfUtc);

        Assert.Equal(40, result.Count);
        Assert.Equal("TOKEN45/EUR", result[0].Symbol);
        Assert.Equal("TOKEN06/EUR", result[^1].Symbol);
        Assert.DoesNotContain(result, candidate => candidate.Symbol == "TOKEN05/EUR");
    }

    [Fact]
    public async Task AutoSelectionRanksOnValidationAndRequiresUntouchedHoldout()
    {
        var fromUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddDays(30);
        var source = new IntervalCandleSource(
            Daily("BTC/EUR", fromUtc, 30, 50_000m, 100m),
            HourlyUptrend("BTC/EUR", fromUtc, 30 * 24));
        var qualification = new PaperTrainingHistoricalQualification(
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
        var selection = new PaperTrainingAutoSelectionService(
            new PaperTrainingUniverseDiscovery(
                new SuppliedPairSource([Pair("BTC/EUR")]),
                source,
                PaperTrainingUniversePolicy.PlatformDefault),
            qualification);

        var result = await selection.SelectAsync(new(
            1_000m,
            [CandleInterval.OneHour],
            fromUtc,
            toUtc,
            PaperTrainingQualificationGate.PlatformDefault));

        Assert.Equal(10, result.Slots.Count);
        Assert.All(result.Slots, slot => Assert.Equal("BTC/EUR", slot.Symbol));
        Assert.Equal(
            result.Slots.Count,
            result.Slots.Select(slot => slot.StrategyId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(result.Slots.Count, result.Qualifications.Count);
        Assert.Contains(result.Qualifications, item => item.Accepted);
        Assert.Contains(result.Qualifications, item => item.PaperOnlyExploration);
        Assert.All(result.Qualifications, item =>
            Assert.NotEqual(item.Accepted, item.PaperOnlyExploration));
        Assert.All(result.Qualifications, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.StrategyId));
            Assert.Contains("holdout", item.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task AutoSelectionFillsTenPaperSlotsWithQualifiedOrExplicitExplorationCandidates()
    {
        var fromUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var toUtc = fromUtc.AddDays(30);
        var source = new GeneratedCandleSource(fromUtc);
        var selection = new PaperTrainingAutoSelectionService(
            new PaperTrainingUniverseDiscovery(
                new SuppliedPairSource([Pair("BTC/EUR"), Pair("ETH/EUR")]),
                source,
                PaperTrainingUniversePolicy.PlatformDefault),
            new PaperTrainingHistoricalQualification(
                source,
                ApprovedExperimentStrategyRegistry.CreatePlatformDefault()));

        var result = await selection.SelectAsync(new(
            1_000m,
            PaperTrainingAutoSelectionService.ApprovedIntervals,
            fromUtc,
            toUtc,
            PaperTrainingQualificationGate.PlatformDefault));

        Assert.Equal(10, result.Slots.Count);
        Assert.Equal(10, result.Qualifications.Count);
        Assert.All(result.Qualifications, qualification =>
            Assert.True(qualification.Accepted || qualification.PaperOnlyExploration));
        Assert.Contains(result.Qualifications, qualification => qualification.PaperOnlyExploration);
        Assert.Equal(
            PaperTrainingAutoSelectionService.ApprovedIntervals.Order(),
            result.Slots.Select(slot => slot.Interval).Distinct().Order());
        Assert.Equal(
            10,
            result.Slots.Select(slot => slot.StrategyId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            result.Slots.GroupBy(slot => slot.StrategyId, StringComparer.Ordinal),
            group => Assert.True(group.Count() <= PaperTrainingAutoSelectionService.MaximumWorkersPerStrategy));
        Assert.Contains(result.Slots, slot => slot.StrategyId is
            "platform.rsi-macd-confluence"
            or "platform.ema-rsi-trend"
            or "platform.bollinger-macd-recovery"
            or "platform.donchian-volume-breakout"
            or "platform.ema-volume-pullback");
    }

    private static TradablePair Pair(string displayName, bool active = true)
    {
        var parts = displayName.Split('/');
        return new TradablePair(
            displayName.Replace("/", string.Empty, StringComparison.Ordinal),
            displayName,
            parts[0],
            parts[1],
            active,
            0.0001m,
            0.0001m,
            0.0001m);
    }

    private static Candle[] Daily(
        string symbol,
        DateTimeOffset asOfUtc,
        int count,
        decimal close,
        decimal volume) =>
        Enumerable.Range(0, count)
            .Select(index =>
            {
                var openTime = asOfUtc.AddDays(index - count);
                return new Candle(
                    symbol,
                    CandleInterval.OneDay,
                    openTime,
                    openTime.AddDays(1),
                    close,
                    close,
                    close,
                    close,
                    volume,
                    true,
                    false);
            })
            .ToArray();

    private static Candle[] HourlyUptrend(string symbol, DateTimeOffset fromUtc, int count)
    {
        var candles = new Candle[count];
        var open = 100m;
        for (var index = 0; index < count; index++)
        {
            candles[index] = new Candle(
                symbol,
                CandleInterval.OneHour,
                fromUtc.AddHours(index),
                fromUtc.AddHours(index + 1),
                open,
                open * 1.03m,
                open * 0.99m,
                open * 1.02m,
                10_000m,
                true,
                false);
            open *= 1.02m;
        }

        return candles;
    }

    private sealed class SuppliedPairSource(IReadOnlyList<TradablePair> pairs) : ITradablePairSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(pairs);
        }
    }

    private sealed class SymbolCandleSource(
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> candles) : IHistoricalCandleSource
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(candles[symbol]);
        }
    }

    private sealed class IntervalCandleSource(
        IReadOnlyList<Candle> daily,
        IReadOnlyList<Candle> hourly) : IHistoricalCandleSource
    {
        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(interval == CandleInterval.OneDay ? daily : hourly);
        }
    }

    private sealed class GeneratedCandleSource(DateTimeOffset evidenceAsOfUtc) : IHistoricalCandleSource
    {
        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Candle> candles = interval == CandleInterval.OneDay
                ? Daily(symbol, evidenceAsOfUtc, 30, 50_000m, 100m)
                : IntervalUptrend(symbol, interval, sinceUtc, 600);
            return Task.FromResult(candles);
        }
    }

    private static Candle[] IntervalUptrend(
        string symbol,
        CandleInterval interval,
        DateTimeOffset fromUtc,
        int count)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var candles = new Candle[count];
        var open = 100m;
        for (var index = 0; index < count; index++)
        {
            var openTime = fromUtc.AddTicks(duration.Ticks * index);
            candles[index] = new Candle(
                symbol,
                interval,
                openTime,
                openTime.Add(duration),
                open,
                open * 1.03m,
                open * 0.99m,
                open * 1.02m,
                10_000m,
                true,
                false);
            open *= 1.02m;
        }

        return candles;
    }
}
