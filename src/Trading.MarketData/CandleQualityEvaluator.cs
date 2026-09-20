namespace Trading.MarketData;

public static class CandleQualityEvaluator
{
    public static IReadOnlyCollection<DataQualityIssue> Evaluate(
        Candle candle,
        Candle? previousCandle,
        DateTimeOffset nowUtc,
        TimeSpan expectedInterval,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(candle);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);

        var issues = new HashSet<DataQualityIssue>(candle.QualityFlags);
        var utcNow = nowUtc.ToUniversalTime();

        if (!candle.IsClosed || candle.CloseTimeUtc > utcNow)
        {
            issues.Add(DataQualityIssue.Incomplete);
        }

        if (candle.IsClosed && utcNow > candle.CloseTimeUtc + expectedInterval)
        {
            issues.Add(DataQualityIssue.Late);
        }

        if (previousCandle is not null
            && string.Equals(previousCandle.Symbol, candle.Symbol, StringComparison.OrdinalIgnoreCase)
            && previousCandle.Interval == candle.Interval)
        {
            if (previousCandle.OpenTimeUtc == candle.OpenTimeUtc)
            {
                issues.Add(DataQualityIssue.Duplicate);
            }
            else if (candle.OpenTimeUtc < previousCandle.OpenTimeUtc)
            {
                issues.Add(DataQualityIssue.OutOfOrder);
            }
            else if (candle.OpenTimeUtc > previousCandle.OpenTimeUtc + expectedInterval)
            {
                issues.Add(DataQualityIssue.Missing);
            }
        }

        if (utcNow - candle.CloseTimeUtc > staleAfter)
        {
            issues.Add(DataQualityIssue.Stale);
        }

        return issues.OrderBy(issue => (int)issue).ToArray();
    }

    public static IReadOnlyList<IReadOnlyCollection<DataQualityIssue>> EvaluateSequence(
        IReadOnlyList<Candle> candles,
        DateTimeOffset nowUtc,
        TimeSpan expectedInterval,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);

        var issues = candles
            .Select(candle => new HashSet<DataQualityIssue>(
                Evaluate(candle ?? throw new ArgumentException("Candle sequence cannot contain null values.", nameof(candles)),
                    null, nowUtc, expectedInterval, staleAfter)))
            .ToArray();

        foreach (var group in candles.Select((candle, index) => (Candle: candle!, Index: index))
                     .GroupBy(item => (item.Candle.Symbol, item.Candle.Interval),
                         new CandleSequenceKeyComparer()))
        {
            Candle? latestObserved = null;
            foreach (var item in group)
            {
                if (latestObserved is not null)
                {
                    if (item.Candle.OpenTimeUtc == latestObserved.OpenTimeUtc)
                    {
                        issues[item.Index].Add(DataQualityIssue.Duplicate);
                    }
                    else if (item.Candle.OpenTimeUtc < latestObserved.OpenTimeUtc)
                    {
                        issues[item.Index].Add(DataQualityIssue.OutOfOrder);
                    }
                }

                if (latestObserved is null || item.Candle.OpenTimeUtc > latestObserved.OpenTimeUtc)
                {
                    latestObserved = item.Candle;
                }
            }

            var chronological = group.OrderBy(item => item.Candle.OpenTimeUtc).ToArray();
            for (var index = 1; index < chronological.Length; index++)
            {
                var previous = chronological[index - 1];
                var current = chronological[index];
                if (current.Candle.OpenTimeUtc > previous.Candle.OpenTimeUtc + expectedInterval)
                {
                    issues[current.Index].Add(DataQualityIssue.Missing);
                }
            }
        }

        return issues
            .Select(set => (IReadOnlyCollection<DataQualityIssue>)Array.AsReadOnly(set.OrderBy(issue => (int)issue).ToArray()))
            .ToArray();
    }

    private sealed class CandleSequenceKeyComparer : IEqualityComparer<(string Symbol, Trading.Domain.Market.CandleInterval Interval)>
    {
        public bool Equals(
            (string Symbol, Trading.Domain.Market.CandleInterval Interval) x,
            (string Symbol, Trading.Domain.Market.CandleInterval Interval) y) =>
            x.Interval == y.Interval && string.Equals(x.Symbol, y.Symbol, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Symbol, Trading.Domain.Market.CandleInterval Interval) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Symbol), value.Interval);
    }
}
