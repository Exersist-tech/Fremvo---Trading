using Trading.Backtesting;

namespace Trading.Optimization;

public enum DatasetSplitType
{
    Training = 0,
    Validation,
    Holdout,
    WalkForward
}

/// <summary>
/// An immutable, dataset-version-bound time window. Ranges are UTC and end-exclusive:
/// [FromUtc, ToUtc). Adjacent windows therefore never share a candle.
/// </summary>
public sealed class DatasetSplit
{
    public DatasetSplit(
        string id,
        DatasetSplitType splitType,
        HistoricalDataset dataset,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int candleCount)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        Id = Required(id, nameof(id));
        if (!Enum.IsDefined(splitType))
        {
            throw new ArgumentOutOfRangeException(nameof(splitType), "Split type is not supported.");
        }

        RequireUtc(fromUtc, nameof(fromUtc));
        RequireUtc(toUtc, nameof(toUtc));
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("Split end time must be after start time.", nameof(toUtc));
        }

        if (candleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candleCount), "Candle count must be positive.");
        }

        var datasetEndExclusive = dataset.ToUtc + HistoricalDataset.GetIntervalDuration(dataset.Interval);
        if (fromUtc < dataset.FromUtc || toUtc > datasetEndExclusive)
        {
            throw new ArgumentException(
                "Split range must be fully contained by the immutable historical dataset.",
                nameof(fromUtc));
        }

        SplitType = splitType;
        DatasetId = dataset.Id;
        DatasetVersionIdentity = dataset.VersionIdentity;
        Source = dataset.Source;
        Symbol = dataset.Symbol;
        Interval = dataset.Interval;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        CandleCount = candleCount;
    }

    public string Id { get; }

    public DatasetSplitType SplitType { get; }

    /// <summary>Caller-facing immutable dataset identifier.</summary>
    public string DatasetId { get; }

    /// <summary>Content-addressed version identity used to prevent cross-dataset mixing.</summary>
    public string DatasetVersionIdentity { get; }

    public string Source { get; }

    public string Symbol { get; }

    public string Interval { get; }

    public DateTimeOffset FromUtc { get; }

    public DateTimeOffset ToUtc { get; }

    public int CandleCount { get; }

    /// <summary>Holdout data is never eligible for parameter selection.</summary>
    public bool IsAvailableForSelection => SplitType != DatasetSplitType.Holdout;

    /// <summary>Holdouts are locked by construction; this value never changes.</summary>
    public bool IsLockedForEvaluation => SplitType == DatasetSplitType.Holdout;

    public bool IsTimeOrderedAfter(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameDataset(other);
        return other.ToUtc <= FromUtc;
    }

    public bool IsNonOverlappingWith(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameDataset(other);
        return other.ToUtc <= FromUtc || other.FromUtc >= ToUtc;
    }

    public bool IsSameSemanticRangeAs(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SplitType == other.SplitType &&
               DatasetVersionIdentity == other.DatasetVersionIdentity &&
               FromUtc == other.FromUtc &&
               ToUtc == other.ToUtc;
    }

    public void ValidateNoFutureLeakage(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameDataset(other);

        if (!IsNonOverlappingWith(other))
        {
            throw new InvalidOperationException("Dataset splits must be non-overlapping to prevent future-data leakage.");
        }
    }

    public void EnsureSameDataset(DatasetSplit other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (DatasetVersionIdentity != other.DatasetVersionIdentity ||
            Source != other.Source ||
            Symbol != other.Symbol ||
            Interval != other.Interval)
        {
            throw new ArgumentException(
                "Dataset splits must use the same immutable dataset version, source, symbol, and interval.",
                nameof(other));
        }
    }

    private static string Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        return value.Trim();
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Split timestamps must be expressed explicitly in UTC.", parameterName);
        }
    }
}
