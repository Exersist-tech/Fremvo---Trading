using Trading.Domain.Market;

namespace Trading.MarketData;

public sealed class Candle
{
    private static readonly IReadOnlyCollection<DataQualityIssue> s_emptyQualityFlags =
        Array.AsReadOnly(Array.Empty<DataQualityIssue>());

    public Candle(
        string symbol,
        CandleInterval interval,
        DateTimeOffset openTimeUtc,
        DateTimeOffset closeTimeUtc,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal volume,
        bool isClosed,
        bool isDerived,
        IReadOnlyCollection<DataQualityIssue>? qualityFlags = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (interval == CandleInterval.None)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval is required.");
        }

        if (openTimeUtc == default || closeTimeUtc == default)
        {
            throw new ArgumentException("Open and close timestamps are required.", nameof(openTimeUtc));
        }

        if (closeTimeUtc <= openTimeUtc)
        {
            throw new ArgumentException("Close time must be after open time.", nameof(closeTimeUtc));
        }

        if (low > high)
        {
            throw new ArgumentException("Low cannot exceed high.", nameof(low));
        }

        if (open < 0m || high < 0m || low < 0m || close < 0m || volume < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(open), "Price and volume must be non-negative.");
        }

        Symbol = symbol.Trim();
        Interval = interval;
        OpenTimeUtc = openTimeUtc.ToUniversalTime();
        CloseTimeUtc = closeTimeUtc.ToUniversalTime();
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        IsClosed = isClosed;
        IsDerived = isDerived;
        QualityFlags = Array.AsReadOnly(
            (qualityFlags ?? s_emptyQualityFlags)
                .Where(issue => issue != DataQualityIssue.None)
                .Append(isClosed ? DataQualityIssue.None : DataQualityIssue.Incomplete)
                .Append(isDerived ? DataQualityIssue.Derived : DataQualityIssue.None)
                .Where(issue => issue != DataQualityIssue.None)
                .Distinct()
                .OrderBy(issue => (int)issue)
                .ToArray());
    }

    public string Symbol { get; }

    public CandleInterval Interval { get; }

    public DateTimeOffset OpenTimeUtc { get; }

    public DateTimeOffset CloseTimeUtc { get; }

    public decimal Open { get; }

    public decimal High { get; }

    public decimal Low { get; }

    public decimal Close { get; }

    public decimal Volume { get; }

    public bool IsClosed { get; }

    public bool IsDerived { get; }

    public IReadOnlyCollection<DataQualityIssue> QualityFlags { get; }

    public bool CanBeUsedForClosedCandleSignal =>
        IsClosed && QualityFlags.All(issue => issue == DataQualityIssue.Derived);
}
