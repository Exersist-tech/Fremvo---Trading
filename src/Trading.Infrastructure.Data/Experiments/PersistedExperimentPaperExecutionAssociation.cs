namespace Trading.Infrastructure.Data.Experiments;

public sealed class PersistedExperimentPaperExecutionAssociation
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
    public string CorrelationId { get; set; } = null!;
    public int Status { get; set; }
    public Guid? ExecutionCommandId { get; set; }
    public string? Detail { get; set; }
}
