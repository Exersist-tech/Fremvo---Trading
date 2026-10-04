using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;

namespace Trading.Application.Experiments;

public sealed record CrossSectionalRankEvidence(
    string Symbol,
    decimal MomentumScore,
    decimal RelativeStrengthScore,
    decimal MediumReturn,
    decimal LongReturn,
    decimal TrendStrength,
    decimal MedianQuoteVolume,
    decimal Volatility,
    decimal CorrelationToBenchmark,
    bool AboveEma200)
{
    public decimal BenchmarkExcessReturn { get; init; }
}

public sealed record CrossSectionalRankingWeights(
    decimal MomentumMedium,
    decimal MomentumLong,
    decimal MomentumTrend,
    decimal MomentumLiquidity,
    decimal MomentumVolatilityPenalty,
    decimal MomentumTurnoverPenalty,
    decimal RelativeMedium,
    decimal RelativeLong,
    decimal RelativeBenchmark,
    decimal RelativeMedian,
    decimal RelativeRiskAdjusted)
{
    public static CrossSectionalRankingWeights PlatformDefault { get; } =
        new(.40m, .25m, .20m, .15m, .10m, .10m, 0m, .30m, .25m, .20m, .25m);
}

public static class CrossSectionalConsensusRanking
{
    public static CrossSectionalRankingWeights ReadWeights(StrategyParameterValues parameters, bool relativeStrength)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var defaults = CrossSectionalRankingWeights.PlatformDefault;
        return relativeStrength
            ? defaults with
            {
                RelativeMedium = parameters.Decimal("relativeMediumWeight"),
                RelativeLong = parameters.Decimal("relativeLongWeight"),
                RelativeBenchmark = parameters.Decimal("relativeBenchmarkWeight"),
                RelativeMedian = parameters.Decimal("relativeMedianWeight"),
                RelativeRiskAdjusted = parameters.Decimal("relativeRiskAdjustedWeight")
            }
            : defaults with
            {
                MomentumMedium = parameters.Decimal("momentumMediumWeight"),
                MomentumLong = parameters.Decimal("momentumLongWeight"),
                MomentumTrend = parameters.Decimal("momentumTrendWeight"),
                MomentumLiquidity = parameters.Decimal("momentumLiquidityWeight"),
                MomentumVolatilityPenalty = parameters.Decimal("momentumVolatilityPenalty"),
                MomentumTurnoverPenalty = parameters.Decimal("momentumTurnoverPenalty")
            };
    }

    public static CrossSectionalRankEvidence[] Evaluate(
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> universe,
        int mediumLookback = 30,
        int longLookback = 90,
        int trendEmaPeriod = 200,
        int volatilityLookback = 90,
        int liquidityLookback = 30,
        CrossSectionalRankingWeights? weights = null,
        bool legacyRanking = false,
        bool dailyExcessBreadth = false)
    {
        ArgumentNullException.ThrowIfNull(universe);
        if (mediumLookback < 1 || longLookback <= mediumLookback || trendEmaPeriod < 2
            || volatilityLookback < 2 || liquidityLookback < 1)
            throw new ArgumentOutOfRangeException(nameof(mediumLookback), "Ranking lookbacks and trend EMA must be ordered positive periods.");
        weights ??= CrossSectionalRankingWeights.PlatformDefault;
        if (weights.MomentumMedium + weights.MomentumLong + weights.MomentumTrend + weights.MomentumLiquidity != 1m
            || weights.RelativeMedium + weights.RelativeLong + weights.RelativeBenchmark
                + weights.RelativeMedian + weights.RelativeRiskAdjusted != 1m)
            throw new ArgumentException("Cross-sectional ranking weights must sum to one.", nameof(weights));
        if (universe.Count == 0
            || universe.Any(item => item.Value.Count < new[]
            {
                longLookback + 1, trendEmaPeriod + 1, volatilityLookback + 1, liquidityLookback
            }.Max() || item.Value.Any(candle => candle.Close <= 0m)))
        {
            return [];
        }

        var raw = universe
            .Select(item => Raw(
                item.Key, item.Value, mediumLookback, longLookback, trendEmaPeriod, volatilityLookback, liquidityLookback))
            .OrderBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var benchmark = raw.FirstOrDefault(item =>
            item.Symbol.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase));
        if (benchmark is null)
            return [];
        if (dailyExcessBreadth && (universe.Any(item =>
                item.Value.Any(candle => !candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal)
                || item.Value.Zip(item.Value.Skip(1),
                    (left, right) => left.CloseTimeUtc == right.OpenTimeUtc).Any(contiguous => !contiguous))
            || raw.Any(item =>
                item.DailyReturns.Length != longLookback
                || item.CloseTimes.Length != benchmark.CloseTimes.Length
                || !item.CloseTimes.SequenceEqual(benchmark.CloseTimes))))
            return [];

        var medianReturn = Median(raw.Select(item => item.LongReturn).ToArray());
        var dailyMedians = dailyExcessBreadth
            ? Enumerable.Range(0, longLookback)
                .Select(index => Median(raw.Select(item => item.DailyReturns[index]).ToArray()))
                .ToArray()
            : [];
        var mediumRanks = Ranks(raw, item => item.MediumReturn, descending: true, legacyRanking);
        var longRanks = Ranks(raw, item => item.LongReturn, descending: true, legacyRanking);
        var mediumRelativeRanks = Ranks(
            raw,
            item => item.MediumReturn - benchmark.MediumReturn,
            descending: true, legacyRanking);
        var trendRanks = Ranks(raw, item => item.TrendStrength, descending: true, legacyRanking);
        var liquidityRanks = Ranks(raw, item => item.MedianQuoteVolume, descending: true, legacyRanking);
        var volatilityRanks = Ranks(raw, item => item.Volatility, descending: false, legacyRanking);
        var btcRelativeRanks = Ranks(raw, item => item.LongReturn - benchmark.LongReturn, descending: true, legacyRanking);
        var medianRelativeRanks = Ranks(raw, item => item.LongReturn - medianReturn, descending: true, legacyRanking);
        var adjustedRanks = Ranks(
            raw,
            item => item.Volatility <= 0m ? decimal.MinValue : item.LongReturn / item.Volatility,
            descending: true, legacyRanking);

        return raw.Select(item =>
        {
            var correlation = item.Symbol.Equals(benchmark.Symbol, StringComparison.OrdinalIgnoreCase)
                ? 1m
                : Correlation(item.Returns, benchmark.Returns);
            var turnoverPenalty = decimal.Abs(mediumRanks[item.Symbol] - longRanks[item.Symbol]);
            var momentumScore =
                weights.MomentumMedium * mediumRanks[item.Symbol]
                + weights.MomentumLong * longRanks[item.Symbol]
                + weights.MomentumTrend * trendRanks[item.Symbol]
                + weights.MomentumLiquidity * liquidityRanks[item.Symbol]
                - weights.MomentumVolatilityPenalty * (legacyRanking
                    ? volatilityRanks[item.Symbol] : 1m - volatilityRanks[item.Symbol])
                - weights.MomentumTurnoverPenalty * turnoverPenalty;
            var relativeScore =
                weights.RelativeMedium * mediumRelativeRanks[item.Symbol]
                + weights.RelativeLong * longRanks[item.Symbol]
                + weights.RelativeBenchmark * (dailyExcessBreadth
                    ? BeatRate(item.DailyReturns, benchmark.DailyReturns)
                    : btcRelativeRanks[item.Symbol])
                + weights.RelativeMedian * (dailyExcessBreadth
                    ? BeatRate(item.DailyReturns, dailyMedians)
                    : medianRelativeRanks[item.Symbol])
                + weights.RelativeRiskAdjusted * adjustedRanks[item.Symbol];
            return new CrossSectionalRankEvidence(
                item.Symbol,
                momentumScore,
                relativeScore,
                item.MediumReturn,
                item.LongReturn,
                item.TrendStrength,
                item.MedianQuoteVolume,
                item.Volatility,
                correlation,
                item.AboveEma200)
            {
                BenchmarkExcessReturn = item.LongReturn - benchmark.LongReturn
            };
        })
        .ToArray();
    }

    private static decimal BeatRate(decimal[] returns, decimal[] reference) =>
        returns.Zip(reference).Count(pair => pair.First > pair.Second) / (decimal)returns.Length;

    private static RawEvidence Raw(
        string symbol,
        IReadOnlyList<Candle> candles,
        int mediumLookback,
        int longLookback,
        int trendEmaPeriod,
        int volatilityLookback,
        int liquidityLookback)
    {
        decimal Return(int periods) => candles[^1].Close / candles[^(periods + 1)].Close - 1m;
        var ema200 = new ExponentialMovingAverageCalculator(trendEmaPeriod).Calculate(candles).Value!.Value;
        var returns = candles.TakeLast(volatilityLookback + 1)
            .Zip(candles.TakeLast(volatilityLookback + 1).Skip(1), static (left, right) => right.Close / left.Close - 1m)
            .ToArray();
        var rankingCandles = candles.TakeLast(longLookback + 1).ToArray();
        var dailyReturns = rankingCandles.Zip(rankingCandles.Skip(1),
            static (left, right) => right.Close / left.Close - 1m).ToArray();
        var quoteVolumes = candles.TakeLast(liquidityLookback)
            .Select(candle => candle.Close * candle.Volume)
            .ToArray();
        return new(
            symbol,
            Return(mediumLookback),
            Return(longLookback),
            candles[^1].Close / ema200 - 1m,
            Median(quoteVolumes),
            StandardDeviation(returns),
            candles[^1].Close > ema200,
            returns,
            dailyReturns,
            rankingCandles.Skip(1).Select(candle => candle.CloseTimeUtc).ToArray());
    }

    private static Dictionary<string, decimal> Ranks(
        IReadOnlyList<RawEvidence> values,
        Func<RawEvidence, decimal> selector,
        bool descending,
        bool legacyRanking)
    {
        var ordered = descending
            ? values.OrderByDescending(selector).ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            : values.OrderBy(selector).ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var ranked = ordered.ToArray();
        if (legacyRanking)
        {
            for (var index = 0; index < ranked.Length; index++)
                result[ranked[index].Symbol] = ranked.Length == 1
                    ? 1m : 1m - index / (decimal)(ranked.Length - 1);
            return result;
        }
        for (var index = 0; index < ranked.Length;)
        {
            var first = index;
            var value = selector(ranked[index]);
            while (index + 1 < ranked.Length && selector(ranked[index + 1]) == value)
                index++;
            var rank = ranked.Length == 1
                ? 1m
                : 1m - (first + index) / (2m * (ranked.Length - 1));
            for (var tied = first; tied <= index; tied++)
                result[ranked[tied].Symbol] = rank;
            index++;
        }
        return result;
    }

    private static decimal Median(decimal[] values)
    {
        Array.Sort(values);
        return values.Length % 2 == 0
            ? (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2m
            : values[values.Length / 2];
    }

    private static decimal StandardDeviation(decimal[] values)
    {
        if (values.Length == 0)
            return decimal.MaxValue;
        var average = values.Average();
        var variance = values.Sum(value => (value - average) * (value - average)) / values.Length;
        return (decimal)Math.Sqrt((double)variance);
    }

    private static decimal Correlation(decimal[] left, decimal[] right)
    {
        var count = Math.Min(left.Length, right.Length);
        if (count < 2)
            return 1m;
        var x = left.TakeLast(count).ToArray();
        var y = right.TakeLast(count).ToArray();
        var xMean = x.Average();
        var yMean = y.Average();
        var covariance = Enumerable.Range(0, count)
            .Sum(index => (x[index] - xMean) * (y[index] - yMean));
        var denominator = (decimal)Math.Sqrt((double)(
            x.Sum(value => (value - xMean) * (value - xMean))
            * y.Sum(value => (value - yMean) * (value - yMean))));
        return denominator == 0m ? 1m : covariance / denominator;
    }

    private sealed record RawEvidence(
        string Symbol,
        decimal MediumReturn,
        decimal LongReturn,
        decimal TrendStrength,
        decimal MedianQuoteVolume,
        decimal Volatility,
        bool AboveEma200,
        decimal[] Returns,
        decimal[] DailyReturns,
        DateTimeOffset[] CloseTimes);
}
