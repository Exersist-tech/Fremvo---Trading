using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class DerivedCandleTests
{
    [Fact]
    public void TenMinuteCandleCanBeDerivedFromTenOneMinuteCandles()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var oneMinuteCandles = Enumerable.Range(0, 10)
            .Select(i => new Candle(
                "BTCUSDT",
                CandleInterval.OneMinute,
                start.AddMinutes(i),
                start.AddMinutes(i + 1),
                100m + i,
                101m + i,
                99m + i,
                100.5m + i,
                2m,
                isClosed: true,
                isDerived: false))
            .ToArray();

        var derived = DerivedCandleBuilder.BuildTenMinuteCandle(oneMinuteCandles, "BTCUSDT", start, start.AddMinutes(10), true);

        Assert.Equal(CandleInterval.TenMinutes, derived.Interval);
        Assert.True(derived.IsDerived);
        Assert.Equal(100m, derived.Open);
        Assert.Equal(110m, derived.High);
        Assert.Equal(99m, derived.Low);
        Assert.Equal(109.5m, derived.Close);
        Assert.Equal(20m, derived.Volume);
        Assert.Contains(DataQualityIssue.Derived, derived.QualityFlags);
    }

    [Fact]
    public void DerivedCandleRequiresExactlyTenOneMinuteCandles()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candles = new[]
        {
            new Candle("BTCUSDT", CandleInterval.OneMinute, start, start.AddMinutes(1), 100m, 101m, 99m, 100.5m, 1m, true, false),
            new Candle("BTCUSDT", CandleInterval.OneMinute, start.AddMinutes(1), start.AddMinutes(2), 100m, 101m, 99m, 100.5m, 1m, true, false)
        };

        Assert.Throws<ArgumentException>(() => DerivedCandleBuilder.BuildTenMinuteCandle(candles, "BTCUSDT", start, start.AddMinutes(2), true));
    }

    [Fact]
    public void DerivedCandlePropagatesUnsafeConstituentEvidenceAndRemainsUnusable()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 10)
            .Select(index => new Candle(
                "BTCUSDT", CandleInterval.OneMinute,
                start.AddMinutes(index),
                start.AddMinutes(index + 1),
                100m, 101m, 99m, 100m, 1m, true, false,
                index == 4 ? new[] { DataQualityIssue.Late } : null))
            .ToArray();

        var derived = DerivedCandleBuilder.BuildTenMinuteCandle(
            candles, "BTCUSDT", start, start.AddMinutes(10), true);

        Assert.Contains(DataQualityIssue.Derived, derived.QualityFlags);
        Assert.Contains(DataQualityIssue.Late, derived.QualityFlags);
        Assert.False(derived.CanBeUsedForClosedCandleSignal);
    }

    [Fact]
    public void DerivedCandleRejectsUnalignedOrNonContiguousConstituents()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 1, 0, TimeSpan.Zero);
        var candles = Enumerable.Range(0, 10)
            .Select(index => new Candle(
                "BTCUSDT", CandleInterval.OneMinute,
                start.AddMinutes(index),
                start.AddMinutes(index + 1),
                100m, 101m, 99m, 100m, 1m, true, false))
            .ToArray();

        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildTenMinuteCandle(candles, "BTCUSDT", start, start.AddMinutes(10), true));
    }

    [Fact]
    public void FourDayCandleCanBeDerivedOnTheUnixEpochBoundary()
    {
        var start = DateTimeOffset.UnixEpoch;
        var source = CreateOneDayCandles(start);

        var derived = DerivedCandleBuilder.BuildFourDayCandle(
            source, "BTCUSDT", start, start.AddDays(4), true);

        Assert.Equal(CandleInterval.FourDays, derived.Interval);
        Assert.True(derived.IsClosed);
        Assert.True(derived.IsDerived);
        Assert.Equal(100m, derived.Open);
        Assert.Equal(104m, derived.High);
        Assert.Equal(99m, derived.Low);
        Assert.Equal(103.5m, derived.Close);
        Assert.Equal(10m, derived.Volume);
        Assert.Equal(start, derived.OpenTimeUtc);
        Assert.Equal(start.AddDays(4), derived.CloseTimeUtc);
        Assert.Equal(new[] { DataQualityIssue.Derived }, derived.QualityFlags);
    }

    [Fact]
    public void FourDayCandleUsesEpochGridAcrossCalendarYearBoundary()
    {
        // 2024-12-31 is 20,088 whole UTC days after 1970-01-01: divisible by four.
        var start = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);

        var derived = DerivedCandleBuilder.BuildFourDayCandle(
            CreateOneDayCandles(start), "BTCUSDT", start, start.AddDays(4), true);

        Assert.Equal(new DateTimeOffset(2025, 1, 4, 0, 0, 0, TimeSpan.Zero), derived.CloseTimeUtc);
    }

    [Fact]
    public void FourDayCandleRejectsEveryPartialOrInvalidConstituentWindow()
    {
        var start = DateTimeOffset.UnixEpoch;
        var valid = CreateOneDayCandles(start);

        Assert.Throws<ArgumentNullException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(null!, "BTCUSDT", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(valid.Take(3).ToArray(), "BTCUSDT", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle([valid[0], valid[1], valid[2], null!], "BTCUSDT", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(valid, " ", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(valid, "BTCUSDT", start, start.AddDays(4), false));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(valid, "BTCUSDT", start.AddDays(1), start.AddDays(5), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(valid, "BTCUSDT", start, start.AddDays(3), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(valid, "BTCUSDT", start, start, true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(
                [valid[0], valid[1], valid[2], CreateOneDayCandles(start.AddDays(4))[0]],
                "BTCUSDT", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(
                [valid[0], valid[1], valid[1], valid[3]],
                "BTCUSDT", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(
                [valid[0], valid[1], valid[2], new Candle("ETHUSDT", CandleInterval.OneDay, start.AddDays(3), start.AddDays(4), 1m, 1m, 1m, 1m, 1m, true, false)],
                "BTCUSDT", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(
                [new Candle("BTCUSDT", CandleInterval.TenMinutes, start, start.AddDays(1), 1m, 1m, 1m, 1m, 1m, true, false), valid[1], valid[2], valid[3]],
                "BTCUSDT", start, start.AddDays(4), true));
        Assert.Throws<ArgumentException>(() =>
            DerivedCandleBuilder.BuildFourDayCandle(
                [valid[0], valid[1], valid[2], new Candle("BTCUSDT", CandleInterval.OneDay, start.AddDays(3), start.AddDays(4), 1m, 1m, 1m, 1m, 1m, false, false)],
                "BTCUSDT", start, start.AddDays(4), true));
    }

    [Fact]
    public void FourDayCandleInheritsUnsafeEvidenceAndIsDeterministic()
    {
        var start = DateTimeOffset.UnixEpoch;
        var source = CreateOneDayCandles(start, DataQualityIssue.Late);

        var first = DerivedCandleBuilder.BuildFourDayCandle(source, "BTCUSDT", start, start.AddDays(4), true);
        var second = DerivedCandleBuilder.BuildFourDayCandle(source.Reverse().ToArray(), "btcusdt", start, start.AddDays(4), true);

        Assert.Equal(first.Open, second.Open);
        Assert.Equal(first.High, second.High);
        Assert.Equal(first.Low, second.Low);
        Assert.Equal(first.Close, second.Close);
        Assert.Equal(first.Volume, second.Volume);
        Assert.Equal(first.QualityFlags, second.QualityFlags);
        Assert.Contains(DataQualityIssue.Late, first.QualityFlags);
        Assert.Contains(DataQualityIssue.Derived, first.QualityFlags);
        Assert.False(first.CanBeUsedForClosedCandleSignal);
    }

    private static Candle[] CreateOneDayCandles(DateTimeOffset start, DataQualityIssue? issue = null) =>
        Enumerable.Range(0, 4)
            .Select(index => new Candle(
                "BTCUSDT",
                CandleInterval.OneDay,
                start.AddDays(index),
                start.AddDays(index + 1),
                100m + index,
                101m + index,
                99m + index,
                100.5m + index,
                1m + index,
                isClosed: true,
                isDerived: false,
                issue is null || index != 2 ? null : new[] { issue.Value }))
            .ToArray();
}
