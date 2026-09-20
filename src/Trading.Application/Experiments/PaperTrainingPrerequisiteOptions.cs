namespace Trading.Application.Experiments;

/// <summary>
/// Deployment-owned prerequisites for paper training. These are never accepted from a browser.
/// Every value defaults to false so an incomplete deployment remains inert.
/// </summary>
public sealed class PaperTrainingPrerequisiteOptions
{
    public const string SectionName = "Experiments:PaperTraining:Prerequisites";

    public bool DurableClosedCandleSource { get; init; }
    public bool ApprovedResearchGroupsAndGates { get; init; }
    public bool WorkerRiskPolicy { get; init; }
    public bool PaperFillPolicy { get; init; }
    public bool OutputLedger { get; init; }
    public bool ProtectiveScheduler { get; init; }

    public PaperTrainingPrerequisites ToPrerequisites() =>
        new(DurableClosedCandleSource, ApprovedResearchGroupsAndGates, WorkerRiskPolicy,
            PaperFillPolicy, OutputLedger, ProtectiveScheduler);
}
