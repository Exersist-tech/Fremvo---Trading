namespace Trading.Strategies.Approvals;

public enum StrategyApprovalActorKind
{
    None = 0,
    Human = 1,
    AutomatedProcess = 2
}

public sealed class StrategyApprovalActor
{
    private StrategyApprovalActor(
        StrategyApprovalActorKind kind,
        Guid? humanUserId,
        string? automatedProcessId)
    {
        Kind = kind;
        HumanUserId = humanUserId;
        AutomatedProcessId = automatedProcessId;
    }

    public StrategyApprovalActorKind Kind { get; }

    public Guid? HumanUserId { get; }

    public string? AutomatedProcessId { get; }

    public bool IsHuman => Kind == StrategyApprovalActorKind.Human;

    public static StrategyApprovalActor Human(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A human approval actor requires a non-empty user id.", nameof(userId));
        }

        return new StrategyApprovalActor(StrategyApprovalActorKind.Human, userId, null);
    }

    public static StrategyApprovalActor AutomatedProcess(string processId)
    {
        if (string.IsNullOrWhiteSpace(processId))
        {
            throw new ArgumentException("An automated process id is required.", nameof(processId));
        }

        return new StrategyApprovalActor(
            StrategyApprovalActorKind.AutomatedProcess,
            null,
            processId.Trim());
    }
}
