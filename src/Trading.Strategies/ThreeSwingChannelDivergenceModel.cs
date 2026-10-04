using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;

namespace Trading.Strategies;

public enum ThreeSwingDivergenceDirection
{
    Neutral = 0,
    Bullish = 1,
    Bearish = 2
}

public sealed record ThreeSwingPivot(
    int CandleIndex,
    DateTimeOffset CloseTimeUtc,
    decimal Price,
    decimal Rsi);

public sealed record ThreeSwingChannelDivergenceEvidence(
    bool IsAvailable,
    ThreeSwingDivergenceDirection Direction,
    IReadOnlyList<ThreeSwingPivot> Pivots,
    bool IsNearFiveMinuteChannel,
    bool IsOneHourContextAligned,
    bool IsFourHourContextAligned,
    bool HasReversalConfirmation,
    bool HasMacdConfirmation,
    decimal? FiveMinuteChannelPositionPercent,
    decimal? OneHourChannelPositionPercent,
    decimal? FourHourChannelPositionPercent,
    decimal? Rsi,
    MacdValue? Macd,
    string Reason);

/// <summary>
/// Detects confirmed, closed-candle three-swing RSI divergence. It is an
/// analysis model only and has no order, sizing, position, or execution access.
/// </summary>
public static class ThreeSwingChannelDivergenceModel
{
    public static ThreeSwingChannelDivergenceEvidence Evaluate(
        IReadOnlyList<Candle> fiveMinute,
        IReadOnlyList<Candle> oneHour,
        IReadOnlyList<Candle> fourHour,
        int rsiPeriod = 14,
        int macdFastPeriod = 12,
        int macdSlowPeriod = 26,
        int macdSignalPeriod = 9,
        int channelPeriod = 50,
        int pivotSideBars = 2,
        int maximumPivotLookback = 120,
        decimal channelProximityPercent = 20m,
        decimal contextBearishMinimumPercent = 60m,
        decimal contextBullishMaximumPercent = 40m,
        decimal minimumRsiDivergencePoints = 0m,
        decimal minimumPriceProgressPercent = 0m)
    {
        ArgumentNullException.ThrowIfNull(fiveMinute);
        ArgumentNullException.ThrowIfNull(oneHour);
        ArgumentNullException.ThrowIfNull(fourHour);
        if (rsiPeriod < 2 || macdFastPeriod < 2 || macdSlowPeriod <= macdFastPeriod || macdSignalPeriod < 2
            || channelPeriod < 2 || pivotSideBars < 1 || maximumPivotLookback < pivotSideBars * 2 + 3
            || channelProximityPercent is <= 0m or > 50m
            || contextBullishMaximumPercent is < 0m or >= 50m
            || contextBearishMinimumPercent is <= 50m or > 100m
            || minimumRsiDivergencePoints < 0m
            || minimumPriceProgressPercent < 0m)
        {
            return Unavailable("Three-swing settings are outside the approved safe ranges.");
        }

        var unavailable = ValidateSeries(fiveMinute, CandleInterval.FiveMinutes, out var reason)
            || ValidateSeries(oneHour, CandleInterval.OneHour, out reason)
            || ValidateSeries(fourHour, CandleInterval.FourHours, out reason);
        if (unavailable)
            return Unavailable(reason);

        var asOfUtc = fiveMinute[^1].CloseTimeUtc;
        if (oneHour[^1].CloseTimeUtc > asOfUtc || fourHour[^1].CloseTimeUtc > asOfUtc)
            return Unavailable("Higher-timeframe candles cannot close after the 5-minute signal boundary.");
        if (!string.Equals(fiveMinute[^1].Symbol, oneHour[^1].Symbol, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(fiveMinute[^1].Symbol, fourHour[^1].Symbol, StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable("All timeframe evidence must refer to the same market.");
        }
        if (fiveMinute.Count < Math.Max(60, rsiPeriod + pivotSideBars * 2 + 3)
            || oneHour.Count < channelPeriod || fourHour.Count < channelPeriod)
            return Unavailable($"At least 60 completed 5-minute candles and {channelPeriod} completed 1-hour and 4-hour candles are required.");

        var highs = FindPivots(fiveMinute, isHigh: true, rsiPeriod, pivotSideBars, maximumPivotLookback);
        var lows = FindPivots(fiveMinute, isHigh: false, rsiPeriod, pivotSideBars, maximumPivotLookback);
        var bearishPivots = highs.TakeLast(3).ToArray();
        var bullishPivots = lows.TakeLast(3).ToArray();
        var bearishDivergence = IsBearishDivergence(
            bearishPivots, minimumRsiDivergencePoints, minimumPriceProgressPercent);
        var bullishDivergence = IsBullishDivergence(
            bullishPivots, minimumRsiDivergencePoints, minimumPriceProgressPercent);

        var direction = bearishDivergence
            ? ThreeSwingDivergenceDirection.Bearish
            : bullishDivergence
                ? ThreeSwingDivergenceDirection.Bullish
                : ThreeSwingDivergenceDirection.Neutral;
        if (bearishDivergence && bullishDivergence)
            direction = ThreeSwingDivergenceDirection.Neutral;

        var selectedPivots = direction switch
        {
            ThreeSwingDivergenceDirection.Bearish => bearishPivots,
            ThreeSwingDivergenceDirection.Bullish => bullishPivots,
            _ => Array.Empty<ThreeSwingPivot>()
        };

        var fiveMinutePosition = ChannelPosition(fiveMinute, channelPeriod);
        var oneHourPosition = ChannelPosition(oneHour, channelPeriod);
        var fourHourPosition = ChannelPosition(fourHour, channelPeriod);
        var latest = fiveMinute[^1];
        var previous = fiveMinute[^2];
        var rsiValue = new RelativeStrengthIndexCalculator(rsiPeriod).Calculate(fiveMinute).Value;
        var macdValue = new MovingAverageConvergenceDivergenceCalculator(
            macdFastPeriod,
            macdSlowPeriod,
            macdSignalPeriod).Calculate(fiveMinute).Value;
        var priorMacd = new MovingAverageConvergenceDivergenceCalculator(
            macdFastPeriod,
            macdSlowPeriod,
            macdSignalPeriod).Calculate(fiveMinute.Take(fiveMinute.Count - 1).ToArray()).Value;

        if (direction == ThreeSwingDivergenceDirection.Neutral
            || fiveMinutePosition is null
            || oneHourPosition is null
            || fourHourPosition is null
            || rsiValue is null
            || macdValue is null
            || priorMacd is null)
        {
            return new ThreeSwingChannelDivergenceEvidence(
                true,
                direction,
                selectedPivots,
                false,
                false,
                false,
                false,
                false,
                fiveMinutePosition?.Percent,
                oneHourPosition?.Percent,
                fourHourPosition?.Percent,
                rsiValue,
                macdValue,
                "No confirmed three-swing divergence is present in the recent closed 5-minute candles.");
        }

        var bearish = direction == ThreeSwingDivergenceDirection.Bearish;
        var channelFraction = channelProximityPercent / 100m;
        var isNearSignalChannel = bearish
            ? selectedPivots[^1].Price >= fiveMinutePosition.Value.Upper - fiveMinutePosition.Value.Width * channelFraction
            : selectedPivots[^1].Price <= fiveMinutePosition.Value.Lower + fiveMinutePosition.Value.Width * channelFraction;
        var oneHourAligned = bearish
            ? oneHourPosition.Value.Percent >= contextBearishMinimumPercent
            : oneHourPosition.Value.Percent <= contextBullishMaximumPercent;
        var fourHourAligned = bearish
            ? fourHourPosition.Value.Percent >= contextBearishMinimumPercent
            : fourHourPosition.Value.Percent <= contextBullishMaximumPercent;
        var reversal = bearish
            ? latest.Close < latest.Open && latest.Close < previous.Low
            : latest.Close > latest.Open && latest.Close > previous.High;
        var macdAligned = bearish
            ? macdValue.Value.Line < macdValue.Value.Signal
                && macdValue.Value.Histogram < priorMacd.Value.Histogram
            : macdValue.Value.Line > macdValue.Value.Signal
                && macdValue.Value.Histogram > priorMacd.Value.Histogram;

        return new ThreeSwingChannelDivergenceEvidence(
            true,
            direction,
            selectedPivots,
            isNearSignalChannel,
            oneHourAligned,
            fourHourAligned,
            reversal,
            macdAligned,
            fiveMinutePosition.Value.Percent,
            oneHourPosition.Value.Percent,
            fourHourPosition.Value.Percent,
            rsiValue,
            macdValue,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Three-swing {direction.ToString().ToUpperInvariant()} divergence; 5m channel position {fiveMinutePosition.Value.Percent:F2}%, 1h {oneHourPosition.Value.Percent:F2}%, 4h {fourHourPosition.Value.Percent:F2}%, RSI {rsiValue:F2}, MACD histogram {macdValue.Value.Histogram:F6}."));
    }

    private static ThreeSwingPivot[] FindPivots(
        IReadOnlyList<Candle> candles,
        bool isHigh,
        int rsiPeriod,
        int pivotSideBars,
        int maximumPivotLookback)
    {
        var first = Math.Max(rsiPeriod + pivotSideBars, candles.Count - maximumPivotLookback);
        var last = candles.Count - pivotSideBars - 2;
        var pivots = new List<ThreeSwingPivot>();
        for (var index = first; index <= last; index++)
        {
            var price = isHigh ? candles[index].High : candles[index].Low;
            var isPivot = true;
            for (var offset = 1; offset <= pivotSideBars; offset++)
            {
                var before = isHigh ? candles[index - offset].High : candles[index - offset].Low;
                var after = isHigh ? candles[index + offset].High : candles[index + offset].Low;
                if (isHigh ? price <= before || price <= after : price >= before || price >= after)
                {
                    isPivot = false;
                    break;
                }
            }

            if (!isPivot)
                continue;
            var rsi = new RelativeStrengthIndexCalculator(rsiPeriod)
                .Calculate(candles.Take(index + 1).ToArray()).Value;
            if (rsi.HasValue)
                pivots.Add(new ThreeSwingPivot(index, candles[index].CloseTimeUtc, price, rsi.Value));
        }

        return pivots.ToArray();
    }

    private static bool IsBearishDivergence(
        ThreeSwingPivot[] pivots,
        decimal minimumRsiDifference,
        decimal minimumPriceProgressPercent) =>
        pivots.Length == 3
        && pivots[0].Price <= pivots[1].Price
        && pivots[1].Price <= pivots[2].Price
        && pivots[0].Price < pivots[2].Price
        && (pivots[2].Price / pivots[0].Price - 1m) * 100m >= minimumPriceProgressPercent
        && pivots[0].Rsi - pivots[1].Rsi >= minimumRsiDifference
        && pivots[1].Rsi - pivots[2].Rsi >= minimumRsiDifference
        && pivots[0].Rsi > pivots[1].Rsi
        && pivots[1].Rsi > pivots[2].Rsi;

    private static bool IsBullishDivergence(
        ThreeSwingPivot[] pivots,
        decimal minimumRsiDifference,
        decimal minimumPriceProgressPercent) =>
        pivots.Length == 3
        && pivots[0].Price >= pivots[1].Price
        && pivots[1].Price >= pivots[2].Price
        && pivots[0].Price > pivots[2].Price
        && (pivots[0].Price / pivots[2].Price - 1m) * 100m >= minimumPriceProgressPercent
        && pivots[1].Rsi - pivots[0].Rsi >= minimumRsiDifference
        && pivots[2].Rsi - pivots[1].Rsi >= minimumRsiDifference
        && pivots[0].Rsi < pivots[1].Rsi
        && pivots[1].Rsi < pivots[2].Rsi;

    private static (decimal Lower, decimal Upper, decimal Width, decimal Percent)? ChannelPosition(
        IReadOnlyList<Candle> candles,
        int period)
    {
        var recent = candles.TakeLast(period).ToArray();
        var lower = recent.Min(candle => candle.Low);
        var upper = recent.Max(candle => candle.High);
        var width = upper - lower;
        return width <= 0m
            ? null
            : (lower, upper, width, (candles[^1].Close - lower) / width * 100m);
    }

    private static bool ValidateSeries(
        IReadOnlyList<Candle> candles,
        CandleInterval interval,
        out string reason)
    {
        var expectedDuration = TimeSpan.FromMinutes((int)interval);
        if (candles.Count == 0
            || candles.Any(candle => candle.Interval != interval
                || !candle.CanBeUsedForClosedCandleSignal
                || candle.OpenTimeUtc.Offset != TimeSpan.Zero
                || candle.CloseTimeUtc.Offset != TimeSpan.Zero
                || candle.CloseTimeUtc - candle.OpenTimeUtc != expectedDuration
                || candle.CloseTimeUtc <= candle.OpenTimeUtc)
            || candles.Select(candle => candle.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
            || candles.Zip(candles.Skip(1), static (left, right) =>
                    left.CloseTimeUtc == right.OpenTimeUtc)
                .Any(contiguous => !contiguous))
        {
            reason = $"{interval} evidence must contain only safe, completed, contiguous UTC candles.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static ThreeSwingChannelDivergenceEvidence Unavailable(string reason) =>
        new(
            false,
            ThreeSwingDivergenceDirection.Neutral,
            Array.Empty<ThreeSwingPivot>(),
            false,
            false,
            false,
            false,
            false,
            null,
            null,
            null,
            null,
            null,
            reason);
}
