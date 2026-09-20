namespace Trading.Domain.Scanner;

/// <summary>
/// The platform-defined measurements a scan may evaluate. Values are data,
/// never user-supplied expressions or executable code.
/// </summary>
public enum ScanCriterionKind
{
    None = 0,
    MinimumClosedCandleCount = 1,
    MinimumCandleVolume = 2,
    MaximumCandleRangePercent = 3,
    CloseAboveSimpleMovingAverage = 4
}

/// <summary>
/// An immutable, platform-controlled threshold for a scan.
/// </summary>
public sealed class ScanCriterion : IEquatable<ScanCriterion>
{
    public ScanCriterion(ScanCriterionKind kind, decimal threshold)
    {
        if (!Enum.IsDefined(kind) || kind == ScanCriterionKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "A supported criterion kind is required.");
        }

        if (threshold <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), "A criterion threshold must be positive.");
        }

        if (kind == ScanCriterionKind.MinimumClosedCandleCount &&
            (decimal.Truncate(threshold) != threshold || threshold > 10_000m))
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                "The closed-candle count must be a whole number no greater than 10,000.");
        }

        if (kind == ScanCriterionKind.CloseAboveSimpleMovingAverage &&
            (decimal.Truncate(threshold) != threshold || threshold > 10_000m))
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                "The simple moving average period must be a whole number no greater than 10,000.");
        }

        if (kind == ScanCriterionKind.MaximumCandleRangePercent && threshold > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                "A candle range percentage cannot exceed 100.");
        }

        Kind = kind;
        Threshold = threshold;
    }

    public ScanCriterionKind Kind { get; }

    public decimal Threshold { get; }

    public bool Equals(ScanCriterion? other) =>
        other is not null && Kind == other.Kind && Threshold == other.Threshold;

    public override bool Equals(object? obj) => Equals(obj as ScanCriterion);

    public override int GetHashCode() => HashCode.Combine(Kind, Threshold);
}
