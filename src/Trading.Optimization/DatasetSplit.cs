namespace Trading.Optimization;

public enum DatasetSplitType
{
    Training = 0,
    Validation,
    Holdout,
    WalkForward
}

public sealed class DatasetSplit
{
    public DatasetSplit(
        string id,
        DatasetSplitType splitType,
        string symbol,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int candleCount,
        bool isLockedForEvaluation = false)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Dataset split id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("Split end time must be after start time.", nameof(toUtc));
        }

        if (candleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candleCount), "Candle count cannot be negative.");
        }

        Id = id.Trim();
        SplitType = splitType;
        Symbol = symbol.Trim();
        FromUtc = fromUtc;
        ToUtc = toUtc;
        CandleCount = candleCount;
        IsLockedForEvaluation = isLockedForEvaluation;
    }

    public string Id { get; }

    public DatasetSplitType SplitType { get; }

    public string Symbol { get; }

    public DateTimeOffset FromUtc { get; }

    public DateTimeOffset ToUtc { get; }

    public int CandleCount { get; }

    public bool IsLockedForEvaluation { get; private set; }

    /// <summary>
    /// Returns true when <paramref name="other"/> ends at or before this split begins,
    /// meaning this split contains only data that is strictly in the future relative to
    /// <paramref name="other"/>. Used to prevent look-ahead bias when ordering splits.
    /// </summary>
    public bool IsTimeOrderedAfter(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return other.ToUtc <= FromUtc;
    }

    public bool IsNonOverlappingWith(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return other.ToUtc <= FromUtc || other.FromUtc >= ToUtc;
    }

    public void LockForEvaluation()
    {
        if (SplitType == DatasetSplitType.Holdout)
        {
            IsLockedForEvaluation = true;
            return;
        }

        throw new InvalidOperationException("Only holdout splits can be locked for evaluation.");
    }

    public void ValidateNoFutureLeakage(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Symbol != Symbol)
        {
            return;
        }

        if (other.FromUtc < ToUtc && other.ToUtc > FromUtc)
        {
            throw new InvalidOperationException("Dataset splits must be non-overlapping to prevent future-data leakage.");
        }
    }
}
