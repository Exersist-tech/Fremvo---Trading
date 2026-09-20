namespace Trading.Infrastructure.Data.Experiments;

public sealed class PersistedExperimentResultSnapshot
{
    public Guid OwnerUserId { get; set; }
    public string SnapshotKey { get; set; } = null!;
    public Guid WorkerId { get; set; }
    public int GroupConfigurationVersion { get; set; }
    public string Group { get; set; } = null!;
    public string StrategyId { get; set; } = null!;
    public int StrategyVersion { get; set; }
    public string ParametersFingerprint { get; set; } = null!;
    public string DatasetFingerprint { get; set; } = null!;
    public string ClassifierVersion { get; set; } = null!;
    public string GateEvidenceFingerprint { get; set; } = null!;
    public int Seed { get; set; }
    public string ReproducibilityIdentity { get; set; } = null!;
    public DateTimeOffset EvaluatedAtUtc { get; set; }
    public decimal? Equity { get; set; }
    public decimal Cash { get; set; }
    public decimal PositionQuantity { get; set; }
    public decimal RealizedProfitAndLoss { get; set; }
    public decimal? UnrealizedProfitAndLoss { get; set; }
    public decimal? MaximumDrawdown { get; set; }
    public decimal Fees { get; set; }
    public decimal? Slippage { get; set; }
    public int? RejectedFillCount { get; set; }
    public int? RejectedActionCount { get; set; }
    public decimal? Exposure { get; set; }
    public int? GateFailureCount { get; set; }
}
