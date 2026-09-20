namespace Trading.Indicators;

/// <summary>Represents an indicator value that is unavailable until its warm-up history is complete.</summary>
public sealed record IndicatorResult<T>(T? Value, int RequiredCandleCount, int AvailableCandleCount)
    where T : struct
{
    public bool IsReady => Value.HasValue;
}

public static class IndicatorResults
{
    public static IndicatorResult<T> InsufficientHistory<T>(int requiredCandleCount, int availableCandleCount)
        where T : struct =>
        new(null, requiredCandleCount, availableCandleCount);

    public static IndicatorResult<T> Ready<T>(T value, int requiredCandleCount, int availableCandleCount)
        where T : struct =>
        new(value, requiredCandleCount, availableCandleCount);
}
