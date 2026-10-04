using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class ThreeSwingChannelDivergenceModelTests
{
    private static readonly DateTimeOffset AsOfUtc =
        new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClosedSeriesWithoutThreeSwingPatternRemainNeutral()
    {
        var result = ThreeSwingChannelDivergenceModel.Evaluate(
            Rising(CandleInterval.FiveMinutes, 220),
            Rising(CandleInterval.OneHour, 60),
            Rising(CandleInterval.FourHours, 60));

        Assert.True(result.IsAvailable);
        Assert.Equal(ThreeSwingDivergenceDirection.Neutral, result.Direction);
        Assert.Empty(result.Pivots);
    }

    [Fact]
    public void IncompleteSignalCandleIsRejected()
    {
        var candles = Rising(CandleInterval.FiveMinutes, 220).ToArray();
        var last = candles[^1];
        candles[^1] = new Candle(
            last.Symbol,
            last.Interval,
            last.OpenTimeUtc,
            last.CloseTimeUtc,
            last.Open,
            last.High,
            last.Low,
            last.Close,
            last.Volume,
            false,
            false);

        var result = ThreeSwingChannelDivergenceModel.Evaluate(
            candles,
            Rising(CandleInterval.OneHour, 60),
            Rising(CandleInterval.FourHours, 60));

        Assert.False(result.IsAvailable);
        Assert.Contains("safe, completed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedHigherHighsWithLowerRsiPivotsAreIdentifiedAsBearishDivergence()
    {
        var result = ThreeSwingChannelDivergenceModel.Evaluate(
            BearishDivergenceCandles(),
            Rising(CandleInterval.OneHour, 60),
            Rising(CandleInterval.FourHours, 60));

        Assert.True(result.IsAvailable);
        Assert.Equal(ThreeSwingDivergenceDirection.Bearish, result.Direction);
        Assert.Equal(3, result.Pivots.Count);
        Assert.True(result.Pivots[0].Price < result.Pivots[1].Price);
        Assert.True(result.Pivots[1].Price < result.Pivots[2].Price);
        Assert.True(result.Pivots[0].Rsi > result.Pivots[1].Rsi);
        Assert.True(result.Pivots[1].Rsi > result.Pivots[2].Rsi);
    }

    [Fact]
    public void ConfirmedLowerLowsWithHigherRsiPivotsRequireClosedBullishReversalAndContext()
    {
        var (signal, hourly, fourHourly) = BullishEvidence();
        var result = ThreeSwingChannelDivergenceModel.Evaluate(signal, hourly, fourHourly);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(ThreeSwingDivergenceDirection.Bullish, result.Direction);
        Assert.Equal(3, result.Pivots.Count);
        Assert.True(result.Pivots[0].Price > result.Pivots[1].Price);
        Assert.True(result.Pivots[1].Price > result.Pivots[2].Price);
        Assert.True(result.Pivots[0].Rsi < result.Pivots[1].Rsi);
        Assert.True(result.Pivots[1].Rsi < result.Pivots[2].Rsi);
        Assert.True(result.IsNearFiveMinuteChannel);
        Assert.True(result.IsOneHourContextAligned);
        Assert.True(result.IsFourHourContextAligned);
        Assert.True(result.HasReversalConfirmation);
        Assert.True(result.HasMacdConfirmation);
    }

    internal static (Candle[] Signal, Candle[] Hourly, Candle[] FourHourly) BullishEvidence()
    {
        var pattern = BearishDivergenceCandles().Select((candle, index) =>
        {
            var close = index is >= 156 and <= 170
                ? 110m - (index - 156) * 20m / 14m : 200m - candle.Close;
            var open = index == 219 ? close - 2m : close;
            return new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, open,
                Math.Max(200m - candle.Low, close + 1m),
                Math.Min(200m - candle.High, Math.Min(open, close) - 1m),
                close, candle.Volume, true, false);
        }).ToArray();
        var prefix = Enumerable.Range(0, 100).Select(index =>
        {
            var open = AsOfUtc.AddMinutes((index - 320) * 5);
            return new Candle("XBT/EUR", CandleInterval.FiveMinutes,
                open, open.AddMinutes(5), 100m, 101m, 99m, 100m, 1m, true, false);
        });
        var hourly = FlatContext(CandleInterval.OneHour);
        var fourHourly = FlatContext(CandleInterval.FourHours);
        return ([.. prefix, .. pattern], hourly, fourHourly);
    }

    private static Candle[] FlatContext(CandleInterval interval) =>
        Rising(interval, 320).Select(candle =>
            new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 99m, 102m, 98m, 99m, candle.Volume,
                true, false)).ToArray();

    [Fact]
    public void ConfiguredMinimumPriceProgressFiltersWeakThreeSwingPatterns()
    {
        var result = ThreeSwingChannelDivergenceModel.Evaluate(
            BearishDivergenceCandles(),
            Rising(CandleInterval.OneHour, 60),
            Rising(CandleInterval.FourHours, 60),
            minimumPriceProgressPercent: 10m);

        Assert.True(result.IsAvailable);
        Assert.Equal(ThreeSwingDivergenceDirection.Neutral, result.Direction);
    }

    private static Candle[] Rising(CandleInterval interval, int count)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var close = AlignDown(AsOfUtc, duration);
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var open = close.AddTicks(duration.Ticks * (index - count));
                var price = 100m + index;
                return new Candle(
                    "XBT/EUR",
                    interval,
                    open,
                    open.Add(duration),
                    price,
                    price + 1m,
                    price - 1m,
                    price + .5m,
                    1m,
                    true,
                    false);
            })
            .ToArray();
    }

    private static Candle[] BearishDivergenceCandles()
    {
        const int count = 220;
        var interval = CandleInterval.FiveMinutes;
        var duration = TimeSpan.FromMinutes((int)interval);
        var closeBoundary = AlignDown(AsOfUtc, duration);
        var closes = Enumerable.Repeat(100m, count).ToArray();

        for (var index = 171; index <= 187; index++)
            closes[index] = 110m - (index - 170);
        closes[188] = 96m;
        closes[189] = 98m;
        closes[190] = 97m;
        for (var index = 191; index <= 207; index++)
            closes[index] = 97m - (index - 190);
        closes[208] = 81m;
        closes[209] = 83m;
        closes[210] = 82m;
        for (var index = 211; index <= 218; index++)
            closes[index] = 82m - ((index - 210) * 0.75m);
        closes[219] = 70m;

        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var open = closeBoundary.AddTicks(duration.Ticks * (index - count));
                var close = closes[index];
                var high = close + 1m;
                var low = close - 1m;
                if (index == 170) high = 120m;
                if (index == 190) high = 121m;
                if (index == 210) high = 122m;
                return new Candle(
                    "XBT/EUR",
                    interval,
                    open,
                    open.Add(duration),
                    close,
                    high,
                    index == 219 ? 69m : low,
                    close,
                    1m,
                    true,
                    false);
            })
            .ToArray();
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - value.UtcTicks % interval.Ticks, TimeSpan.Zero);
}
