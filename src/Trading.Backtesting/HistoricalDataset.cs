namespace Trading.Backtesting;

public sealed class HistoricalDataset
{
    public HistoricalDataset(
        string id,
        string symbol,
        string timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int candleCount,
        bool isImmutable)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Dataset id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (string.IsNullOrWhiteSpace(timeframe))
        {
            throw new ArgumentException("Timeframe is required.", nameof(timeframe));
        }

        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("Dataset end time must be after start time.", nameof(toUtc));
        }

        if (candleCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candleCount), "Candle count cannot be negative.");
        }

        Id = id.Trim();
        Symbol = symbol.Trim();
        Timeframe = timeframe.Trim();
        FromUtc = fromUtc;
        ToUtc = toUtc;
        CandleCount = candleCount;
        IsImmutable = isImmutable;
    }

    public string Id { get; }

    public string Symbol { get; }

    public string Timeframe { get; }

    public DateTimeOffset FromUtc { get; }

    public DateTimeOffset ToUtc { get; }

    public int CandleCount { get; }

    public bool IsImmutable { get; }
}
