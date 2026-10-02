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
    bool AboveEma200);

public static class CrossSectionalConsensusRanking
{
    public static CrossSectionalRankEvidence[] Evaluate(
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> universe)
    {
        ArgumentNullException.ThrowIfNull(universe);
        if (universe.Count == 0
            || universe.Any(item => item.Value.Count < 201))
        {
            return [];
        }

        var raw = universe
            .Select(item => Raw(item.Key, item.Value))
            .OrderBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var benchmark = raw.FirstOrDefault(item =>
            item.Symbol.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase));
        if (benchmark is null)
            return [];

        var medianReturn = Median(raw.Select(item => item.LongReturn).ToArray());
        var mediumRanks = Ranks(raw, item => item.MediumReturn, descending: true);
        var longRanks = Ranks(raw, item => item.LongReturn, descending: true);
        var trendRanks = Ranks(raw, item => item.TrendStrength, descending: true);
        var liquidityRanks = Ranks(raw, item => item.MedianQuoteVolume, descending: true);
        var volatilityRanks = Ranks(raw, item => item.Volatility, descending: false);
        var btcRelativeRanks = Ranks(raw, item => item.LongReturn - benchmark.LongReturn, descending: true);
        var medianRelativeRanks = Ranks(raw, item => item.LongReturn - medianReturn, descending: true);
        var adjustedRanks = Ranks(
            raw,
            item => item.Volatility <= 0m ? decimal.MinValue : item.LongReturn / item.Volatility,
            descending: true);

        return raw.Select(item =>
        {
            var correlation = item.Symbol.Equals(benchmark.Symbol, StringComparison.OrdinalIgnoreCase)
                ? 1m
                : Correlation(item.Returns, benchmark.Returns);
            var turnoverPenalty = decimal.Abs(mediumRanks[item.Symbol] - longRanks[item.Symbol]);
            var momentumScore =
                .40m * mediumRanks[item.Symbol]
                + .25m * longRanks[item.Symbol]
                + .20m * trendRanks[item.Symbol]
                + .15m * liquidityRanks[item.Symbol]
                - .10m * volatilityRanks[item.Symbol]
                - .10m * turnoverPenalty;
            var relativeScore =
                .30m * longRanks[item.Symbol]
                + .25m * btcRelativeRanks[item.Symbol]
                + .20m * medianRelativeRanks[item.Symbol]
                + .25m * adjustedRanks[item.Symbol];
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
                item.AboveEma200);
        })
        .ToArray();
    }

    private static RawEvidence Raw(string symbol, IReadOnlyList<Candle> candles)
    {
        decimal Return(int periods) => candles[^1].Close / candles[^(periods + 1)].Close - 1m;
        var ema200 = new ExponentialMovingAverageCalculator(200).Calculate(candles).Value!.Value;
        var returns = candles.TakeLast(91)
            .Zip(candles.TakeLast(91).Skip(1), static (left, right) => right.Close / left.Close - 1m)
            .ToArray();
        var quoteVolumes = candles.TakeLast(30)
            .Select(candle => candle.Close * candle.Volume)
            .ToArray();
        return new(
            symbol,
            Return(30),
            Return(90),
            candles[^1].Close / ema200 - 1m,
            Median(quoteVolumes),
            StandardDeviation(returns),
            candles[^1].Close > ema200,
            returns);
    }

    private static Dictionary<string, decimal> Ranks(
        IReadOnlyList<RawEvidence> values,
        Func<RawEvidence, decimal> selector,
        bool descending)
    {
        var ordered = descending
            ? values.OrderByDescending(selector).ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            : values.OrderBy(selector).ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var ranked = ordered.ToArray();
        for (var index = 0; index < ranked.Length; index++)
        {
            result[ranked[index].Symbol] = ranked.Length == 1
                ? 1m
                : 1m - index / (decimal)(ranked.Length - 1);
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
        decimal[] Returns);
}
