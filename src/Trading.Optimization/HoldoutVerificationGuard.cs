namespace Trading.Optimization;

public sealed class HoldoutVerificationGuard
{
    private readonly DatasetSplit _holdout;
    private readonly List<DatasetSplit> _usedForSelection = new();

    public HoldoutVerificationGuard(DatasetSplit holdout)
    {
        ArgumentNullException.ThrowIfNull(holdout);

        if (holdout.SplitType != DatasetSplitType.Holdout)
        {
            throw new ArgumentException("A holdout verification guard requires a holdout split.", nameof(holdout));
        }

        _holdout = holdout;
        _holdout.LockForEvaluation();
    }

    public bool IsVerified { get; private set; }

    public IReadOnlyCollection<DatasetSplit> UsedForSelection => _usedForSelection.AsReadOnly();

    public void RegisterSelectionCandidate(DatasetSplit candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (IsVerified)
        {
            throw new InvalidOperationException("Holdout verification is complete; no further parameter selection is allowed on the holdout data.");
        }

        if (candidate.Symbol != _holdout.Symbol)
        {
            return;
        }

        if (candidate.FromUtc < _holdout.ToUtc && candidate.ToUtc > _holdout.FromUtc)
        {
            throw new InvalidOperationException("Selection candidates must not overlap the holdout window.");
        }

        _usedForSelection.Add(candidate);
    }

    public void VerifyOnce()
    {
        if (IsVerified)
        {
            throw new InvalidOperationException("Holdout verification has already been performed once and cannot be repeated.");
        }

        IsVerified = true;
    }

    public void EnsureSelectionIsForbiddenAfterVerification()
    {
        if (IsVerified)
        {
            throw new InvalidOperationException("Holdout data cannot be used for parameter selection after verification.");
        }
    }
}
