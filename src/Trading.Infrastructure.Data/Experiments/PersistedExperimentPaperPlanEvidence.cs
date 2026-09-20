namespace Trading.Infrastructure.Data.Experiments;

public sealed class PersistedExperimentPaperPlanEvidence
{
    public Guid UserId { get; set; }
    public Guid WorkerId { get; set; }
    public int GroupConfigurationVersion { get; set; }
    public int Group { get; set; }
    public string StrategyId { get; set; } = null!;
    public int StrategyVersion { get; set; }
    public string StrategyFingerprint { get; set; } = null!;
    public string Symbol { get; set; } = null!;
    public int Interval { get; set; }
    public DateTimeOffset OpenTimeUtc { get; set; }
    public DateTimeOffset CloseTimeUtc { get; set; }
    public DateTimeOffset AsOfUtc { get; set; }
    public decimal ProtectiveStopPrice { get; set; }
    public decimal? ConservativeTargetPrice { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}
