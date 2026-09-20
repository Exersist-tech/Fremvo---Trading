namespace Trading.Infrastructure.Data.Experiments;

public sealed class PersistedPaperTrainingActivation
{
    public Guid OwnerUserId { get; set; }
    public int State { get; set; }
    public int SlotCount { get; set; }
    public bool DurableClosedCandleSource { get; set; }
    public bool ApprovedResearchGroupsAndGates { get; set; }
    public bool WorkerRiskPolicy { get; set; }
    public bool PaperFillPolicy { get; set; }
    public bool OutputLedger { get; set; }
    public bool ProtectiveScheduler { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
    public Guid ChangedBy { get; set; }
    public string? ApprovalId { get; set; }
#pragma warning disable CA1819 // EF Core SQL rowversion is represented as a byte array.
    public byte[] RowVersion { get; set; } = [];
#pragma warning restore CA1819
}
