using System.Collections.ObjectModel;

namespace Trading.Strategies.Approvals;

public sealed class StrategyApprovalTransition
{
    internal StrategyApprovalTransition(
        int sequence,
        StrategyApprovalState from,
        StrategyApprovalState to,
        StrategyApprovalActor actor,
        StrategyApprovalActor? approver,
        DateTimeOffset occurredAtUtc)
    {
        Sequence = sequence;
        From = from;
        To = to;
        Actor = actor;
        Approver = approver;
        OccurredAtUtc = occurredAtUtc;
    }

    public int Sequence { get; }

    public StrategyApprovalState From { get; }

    public StrategyApprovalState To { get; }

    public StrategyApprovalActor Actor { get; }

    public StrategyApprovalActor? Approver { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}

public sealed class StrategyApproval
{
    private readonly ReadOnlyCollection<StrategyApprovalTransition> _transitions;

    private StrategyApproval(
        Guid id,
        StrategyVersion strategyVersion,
        StrategyApprovalState state,
        StrategyApprovalActor createdBy,
        DateTimeOffset createdAtUtc,
        StrategyApprovalActor? approvedBy,
        StrategyApprovalRequirements? requirements,
        IEnumerable<StrategyApprovalTransition> transitions)
    {
        Id = id;
        StrategyVersion = strategyVersion;
        State = state;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
        ApprovedBy = approvedBy;
        Requirements = requirements;
        _transitions = new ReadOnlyCollection<StrategyApprovalTransition>(transitions.ToArray());
    }

    public Guid Id { get; }

    public StrategyVersion StrategyVersion { get; }

    public StrategyApprovalState State { get; }

    public StrategyApprovalActor CreatedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public StrategyApprovalActor? ApprovedBy { get; }

    public StrategyApprovalRequirements? Requirements { get; }

    public IReadOnlyList<StrategyApprovalTransition> Transitions => _transitions;

    public bool IsTerminal => State is StrategyApprovalState.Rejected or StrategyApprovalState.Retired;

    public static StrategyApproval CreateDraft(
        Guid id,
        StrategyVersion strategyVersion,
        StrategyApprovalActor createdBy,
        DateTimeOffset createdAtUtc,
        StrategyApprovalRequirements? requirements = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An approval id is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(strategyVersion);
        ArgumentNullException.ThrowIfNull(createdBy);
        StrategyVersion.EnsureUtc(createdAtUtc, nameof(createdAtUtc));

        return new StrategyApproval(
            id,
            strategyVersion,
            StrategyApprovalState.Draft,
            createdBy,
            createdAtUtc,
            null,
            requirements,
            Array.Empty<StrategyApprovalTransition>());
    }

    public StrategyApproval TransitionTo(
        StrategyApprovalState target,
        StrategyApprovalActor actor,
        DateTimeOffset occurredAtUtc,
        StrategyApprovalActor? approver = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        StrategyVersion.EnsureUtc(occurredAtUtc, nameof(occurredAtUtc));

        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target), "The approval state is not defined.");
        }

        if (target == State || IsTerminal || !IsLegalTransition(State, target))
        {
            throw new InvalidOperationException($"Cannot transition a strategy approval from {State} to {target}.");
        }

        var lastRecordedAtUtc = _transitions.Count == 0
            ? CreatedAtUtc
            : _transitions[^1].OccurredAtUtc;
        if (occurredAtUtc < lastRecordedAtUtc)
        {
            throw new ArgumentException("Approval transitions cannot be backdated.", nameof(occurredAtUtc));
        }

        if (target == StrategyApprovalState.Approved)
        {
            ValidateHumanApproval(actor, approver);
            if (Requirements is null)
            {
                throw new InvalidOperationException(
                    "A strategy approval requires explicit restrictive requirements before approval.");
            }
        }
        else if (approver is not null)
        {
            throw new ArgumentException("Only approval transitions may record an approver.", nameof(approver));
        }

        var transition = new StrategyApprovalTransition(
            _transitions.Count + 1,
            State,
            target,
            actor,
            approver,
            occurredAtUtc);

        return new StrategyApproval(
            Id,
            StrategyVersion,
            target,
            CreatedBy,
            CreatedAtUtc,
            target == StrategyApprovalState.Approved ? approver : ApprovedBy,
            Requirements,
            _transitions.Append(transition));
    }

    private static bool IsLegalTransition(StrategyApprovalState from, StrategyApprovalState to) =>
        (from, to) switch
        {
            (StrategyApprovalState.Draft, StrategyApprovalState.UnderReview) => true,
            (StrategyApprovalState.Draft, StrategyApprovalState.Retired) => true,
            (StrategyApprovalState.UnderReview, StrategyApprovalState.Approved) => true,
            (StrategyApprovalState.UnderReview, StrategyApprovalState.Rejected) => true,
            (StrategyApprovalState.UnderReview, StrategyApprovalState.Retired) => true,
            (StrategyApprovalState.Approved, StrategyApprovalState.Retired) => true,
            _ => false
        };

    private static void ValidateHumanApproval(
        StrategyApprovalActor actor,
        StrategyApprovalActor? approver)
    {
        if (!actor.IsHuman || approver is null || !approver.IsHuman)
        {
            throw new InvalidOperationException(
                "Only an audited human actor and human approver can approve a strategy version.");
        }

        if (actor.HumanUserId != approver.HumanUserId)
        {
            throw new InvalidOperationException(
                "The audited approval actor and approver must identify the same human.");
        }
    }
}
