namespace Trading.Infrastructure.Data.Scanner;

public sealed class PersistedScanResult
{
    public Guid OwnerId { get; set; }

    public Guid ScanRequestId { get; set; }

    public Guid ScanRunId { get; set; }

    public string Symbol { get; set; } = null!;

    public int Rank { get; set; }

    public decimal Score { get; set; }

    public string MatchedCriteria { get; set; } = null!;

    public DateTimeOffset EvidenceAsOfUtc { get; set; }

    public DateTimeOffset EvaluatedAtUtc { get; set; }
}
