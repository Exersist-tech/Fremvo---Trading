namespace Trading.Infrastructure.Data.Backtesting;

public sealed class PersistedHistoricalDataset
{
    public string VersionIdentity { get; set; } = null!;

    public string Id { get; set; } = null!;

    public string Source { get; set; } = null!;

    public string Symbol { get; set; } = null!;

    public string Interval { get; set; } = null!;

    public DateTimeOffset FromUtc { get; set; }

    public DateTimeOffset ToUtc { get; set; }

    public int CandleCount { get; set; }

    public string ContentFingerprint { get; set; } = null!;

    public string SourceVersion { get; set; } = null!;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool ContainsOnlyClosedCandles { get; set; }
}
