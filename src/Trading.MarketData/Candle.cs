using Trading.Domain.Market;

namespace Trading.MarketData;

public sealed class Candle
{
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
        OpenTimeUtc = openTimeUtc;
        CloseTimeUtc = closeTimeUtc;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        IsClosed = isClosed;
        IsDerived = isDerived;
        QualityFlags = qualityFlags ?? Array.Empty<DataQualityIssue>();
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

    public bool CanBeUsedForClosedCandleSignal => IsClosed && !QualityFlags.Contains(DataQualityIssue.Incomplete) && !QualityFlags.Contains(DataQualityIssue.Stale);
}
