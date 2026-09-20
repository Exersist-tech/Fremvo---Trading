using Trading.Domain.Market;
using Trading.Domain.Strategies;

namespace Trading.Domain.Universe;

/// <summary>
/// What an eligibility grant permits. Deliberately separate from
/// <see cref="TradingMode"/>: a grant is held per purpose, and a purpose can
/// be lost without losing the ones below it being lost too.
/// </summary>
public enum EligibilityPurpose
{
    /// <summary>
    /// No purpose. Never a valid scope for a grant; present so the default
    /// value of the enum cannot be mistaken for a real permission.
    /// </summary>
    None = 0,
    Research = 1,
    Backtest = 2,
    Paper = 3,
    SpotTest = 4,
    SpotLive = 5,
    FuturesTest = 6,
    FuturesLive = 7
}

/// <summary>
/// Identifies one eligibility question. Eligibility is never a single global
/// boolean: a pair accepted for a 4-hour strategy is not thereby accepted for
/// a 1-minute strategy, and a pair suitable for backtesting is not thereby
/// suitable for live trading.
/// </summary>
public readonly record struct EligibilityScope(
    EligibilityPurpose Purpose,
    CandleInterval Interval,
    TradingProductType ProductType)
{
    /// <summary>
    /// True when the purpose places real orders on an exchange with real
    /// funds. These purposes always require an explicit human approval.
    /// </summary>
    public bool IsLive => Purpose is EligibilityPurpose.SpotLive or EligibilityPurpose.FuturesLive;

    public override string ToString() => $"{Purpose}/{Interval}/{ProductType}";
}

/// <summary>
/// A granted permission for one <see cref="EligibilityScope"/>, together with
/// the age of the evidence that justified it.
/// </summary>
/// <remarks>
/// A grant is not permanent. It carries the timestamp of the evidence that
/// produced it and expires once that evidence is older than the configured
/// maximum age, so stale measurements can never keep an instrument eligible.
/// </remarks>
public sealed class InstrumentEligibilityGrant
{
    public InstrumentEligibilityGrant(
        EligibilityScope scope,
        DateTimeOffset grantedAtUtc,
        DateTimeOffset evidenceAsOfUtc,
        Guid? approvedByUserId)
    {
        if (scope.Purpose == EligibilityPurpose.None)
        {
            throw new ArgumentException("An eligibility grant requires a concrete purpose.", nameof(scope));
        }

        if (scope.Interval == CandleInterval.None)
        {
            throw new ArgumentException("An eligibility grant requires a concrete interval.", nameof(scope));
        }

        if (evidenceAsOfUtc > grantedAtUtc)
        {
            throw new ArgumentException(
                "Evidence cannot be newer than the grant that relies on it.",
                nameof(evidenceAsOfUtc));
        }

        if (scope.IsLive && approvedByUserId is null)
        {
            throw new ArgumentException(
                "A live-trading grant requires an explicit approving administrator.",
                nameof(approvedByUserId));
        }

        if (approvedByUserId == Guid.Empty)
        {
            throw new ArgumentException("An approving user id cannot be empty.", nameof(approvedByUserId));
        }

        Scope = scope;
        GrantedAtUtc = grantedAtUtc;
        EvidenceAsOfUtc = evidenceAsOfUtc;
        ApprovedByUserId = approvedByUserId;
    }

    public EligibilityScope Scope { get; }

    public DateTimeOffset GrantedAtUtc { get; }

    /// <summary>
    /// The observation time of the measurements that justified this grant.
    /// </summary>
    public DateTimeOffset EvidenceAsOfUtc { get; }

    /// <summary>
    /// The administrator who approved a live grant. Null for non-live grants.
    /// </summary>
    public Guid? ApprovedByUserId { get; }

    /// <summary>
    /// True when the supporting evidence is older than the configured maximum
    /// age. An expired grant confers nothing.
    /// </summary>
    public bool IsExpired(DateTimeOffset nowUtc, TimeSpan maximumEvidenceAge) =>
        nowUtc - EvidenceAsOfUtc > maximumEvidenceAge;
}
