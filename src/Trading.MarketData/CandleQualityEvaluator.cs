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
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(staleAfter, TimeSpan.Zero);

        var issues = new HashSet<DataQualityIssue>();

        if (candle.IsDerived)
        {
            issues.Add(DataQualityIssue.Derived);
        }

        if (!candle.IsClosed)
        {
            issues.Add(DataQualityIssue.Incomplete);
        }

        if (candle.CloseTimeUtc > nowUtc)
        {
            issues.Add(DataQualityIssue.Late);
        }

        if (previousCandle is not null)
        {
            if (string.Equals(previousCandle.Symbol, candle.Symbol, StringComparison.OrdinalIgnoreCase)
                && previousCandle.OpenTimeUtc == candle.OpenTimeUtc)
            {
                issues.Add(DataQualityIssue.Duplicate);
            }

            if (candle.OpenTimeUtc < previousCandle.CloseTimeUtc)
            {
                issues.Add(DataQualityIssue.OutOfOrder);
            }

            if (candle.OpenTimeUtc > previousCandle.CloseTimeUtc
                && candle.OpenTimeUtc - previousCandle.CloseTimeUtc > expectedInterval.Add(TimeSpan.FromSeconds(1)))
            {
                issues.Add(DataQualityIssue.Missing);
            }
        }

        if (nowUtc - candle.CloseTimeUtc > staleAfter)
        {
            issues.Add(DataQualityIssue.Stale);
        }

        return issues;
    }
}
