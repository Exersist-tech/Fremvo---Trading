namespace Trading.Domain.Universe;

/// <summary>
/// Rolling measurements for one instrument over one observation window.
/// </summary>
/// <remarks>
/// Every monetary and quantity value is <see cref="decimal"/> and every
/// timestamp is UTC.
///
/// Both a rolling and a median liquidity measure are carried deliberately.
/// A single current 24-hour volume figure must never grant eligibility: an
/// instrument whose volume is carried by one spike day passes the rolling
/// test and fails the median test, and the median test is what the gate
/// engine requires.
/// </remarks>
public sealed class InstrumentMetrics
{
    public InstrumentMetrics(
        Guid instrumentId,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc,
        DateTimeOffset computedAtUtc,
        decimal rollingQuoteVolume,
        decimal medianQuoteVolume,
        decimal minimumQuoteVolume,
        decimal averageSpread,
        decimal worstSpread,
        decimal estimatedSlippage,
        decimal tradeFrequency,
        int dataGapCount,
        int staleEventCount,
        decimal? priceDepth = null)
    {
        if (instrumentId == Guid.Empty)
        {
            throw new ArgumentException("Instrument id is required.", nameof(instrumentId));
        }

        if (windowEndUtc <= windowStartUtc)
        {
            throw new ArgumentException("The observation window must be positive.", nameof(windowEndUtc));
        }

        if (computedAtUtc < windowEndUtc)
        {
            throw new ArgumentException(
                "Metrics cannot be computed before the end of the window they describe.",
                nameof(computedAtUtc));
        }

        RequireNotNegative(rollingQuoteVolume, nameof(rollingQuoteVolume));
        RequireNotNegative(medianQuoteVolume, nameof(medianQuoteVolume));
        RequireNotNegative(minimumQuoteVolume, nameof(minimumQuoteVolume));
        RequireNotNegative(averageSpread, nameof(averageSpread));
        RequireNotNegative(worstSpread, nameof(worstSpread));
        RequireNotNegative(estimatedSlippage, nameof(estimatedSlippage));
        RequireNotNegative(tradeFrequency, nameof(tradeFrequency));

        if (dataGapCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataGapCount), "Data gap count cannot be negative.");
        }

        if (staleEventCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(staleEventCount), "Stale event count cannot be negative.");
        }

        if (worstSpread < averageSpread)
        {
            throw new ArgumentException(
                "The worst observed spread cannot be tighter than the average spread.",
                nameof(worstSpread));
        }

        if (priceDepth is < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(priceDepth), "Price depth cannot be negative.");
        }

        InstrumentId = instrumentId;
        WindowStartUtc = windowStartUtc;
        WindowEndUtc = windowEndUtc;
        ComputedAtUtc = computedAtUtc;
        RollingQuoteVolume = rollingQuoteVolume;
        MedianQuoteVolume = medianQuoteVolume;
        MinimumQuoteVolume = minimumQuoteVolume;
        AverageSpread = averageSpread;
        WorstSpread = worstSpread;
        EstimatedSlippage = estimatedSlippage;
        TradeFrequency = tradeFrequency;
        DataGapCount = dataGapCount;
        StaleEventCount = staleEventCount;
        PriceDepth = priceDepth;
    }

    public Guid InstrumentId { get; }

    public DateTimeOffset WindowStartUtc { get; }

    public DateTimeOffset WindowEndUtc { get; }

    public DateTimeOffset ComputedAtUtc { get; }

    public decimal RollingQuoteVolume { get; }

    /// <summary>
    /// Median quote volume across the window. Resistant to a single spike,
    /// and therefore the measure the liquidity gate relies on.
    /// </summary>
    public decimal MedianQuoteVolume { get; }

    public decimal MinimumQuoteVolume { get; }

    public decimal AverageSpread { get; }

    public decimal WorstSpread { get; }

    public decimal EstimatedSlippage { get; }

    public decimal TradeFrequency { get; }

    public int DataGapCount { get; }

    public int StaleEventCount { get; }

    /// <summary>
    /// Price depth, when the exchange supplies it. Null means "not measured",
    /// which is not the same as zero depth and must not be treated as such.
    /// </summary>
    public decimal? PriceDepth { get; }

    public bool IsDataHealthy => DataGapCount == 0 && StaleEventCount == 0;

    /// <summary>
    /// True when these measurements are older than the configured maximum
    /// evidence age. Stale measurements can never support a grant.
    /// </summary>
    public bool IsStale(DateTimeOffset nowUtc, TimeSpan maximumEvidenceAge) =>
        nowUtc - ComputedAtUtc > maximumEvidenceAge;

    /// <summary>
    /// Computes the median of a sample without mutating the caller's data.
    /// Uses <see cref="decimal"/> arithmetic throughout.
    /// </summary>
    public static decimal Median(IEnumerable<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("A median requires at least one observation.", nameof(values));
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2m;
    }

    private static void RequireNotNegative(decimal value, string parameterName)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} cannot be negative.");
        }
    }
}
