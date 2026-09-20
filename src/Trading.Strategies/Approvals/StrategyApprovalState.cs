namespace Trading.Strategies.Approvals;

public enum StrategyApprovalState
{
    None = 0,
    Draft = 1,
    UnderReview = 2,
    Approved = 3,
    Rejected = 4,
    Retired = 5
}
