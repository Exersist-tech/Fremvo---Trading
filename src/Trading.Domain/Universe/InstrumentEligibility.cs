using Trading.Domain.Market;
using Trading.Domain.Strategies;

namespace Trading.Domain.Universe;

/// <summary>
/// The set of eligibility grants held by one instrument.
/// </summary>
/// <remarks>
/// Two rules are enforced here rather than left to callers, because both are
/// safety properties:
///
/// 1. Grants are strictly additive. A purpose cannot be granted unless every
///    prerequisite purpose is currently granted for the same interval and
///    product type.
/// 2. Revocation cascades. Revoking a lower purpose revokes every purpose
///    above it in the same operation, so an instrument can never keep a
///    live-trading grant after losing the paper-trading grant beneath it.
/// </remarks>
public sealed class InstrumentEligibility
{
    private readonly Dictionary<EligibilityScope, InstrumentEligibilityGrant> _grants = new();

    public InstrumentEligibility(Guid instrumentId)
    {
        if (instrumentId == Guid.Empty)
        {
            throw new ArgumentException("Instrument id is required.", nameof(instrumentId));
        }

        InstrumentId = instrumentId;
    }

    public Guid InstrumentId { get; }

    public IReadOnlyCollection<InstrumentEligibilityGrant> Grants => _grants.Values;

    /// <summary>
    /// The purpose that must already be granted before <paramref name="purpose"/>
    /// may be granted, or null when the purpose is the entry point.
    /// </summary>
    public static EligibilityPurpose? Prerequisite(EligibilityPurpose purpose) => purpose switch
    {
        EligibilityPurpose.Research => null,        EligibilityPurpose.Backtest => EligibilityPurpose.Research,
        EligibilityPurpose.Paper => EligibilityPurpose.Backtest,
        EligibilityPurpose.SpotTest => EligibilityPurpose.Paper,
        EligibilityPurpose.SpotLive => EligibilityPurpose.SpotTest,

        // Futures branches from paper trading, not from the Spot live grant,
        // because Spot and Futures are separate capabilities.
        EligibilityPurpose.FuturesTest => EligibilityPurpose.Paper,
        EligibilityPurpose.FuturesLive => EligibilityPurpose.FuturesTest,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown eligibility purpose.")
    };

    /// <summary>
    /// Grants a purpose. Fails when a prerequisite is missing or expired,
    /// which makes it impossible to grant live trading to an instrument that
    /// is not currently paper-eligible.
    /// </summary>
    public void Grant(
        EligibilityScope scope,
        DateTimeOffset grantedAtUtc,
        DateTimeOffset evidenceAsOfUtc,
        TimeSpan maximumEvidenceAge,
        Guid? approvedByUserId = null)
    {
        var prerequisite = Prerequisite(scope.Purpose);
        if (prerequisite is not null)
        {
            var prerequisiteScope = scope with { Purpose = prerequisite.Value };
            if (!IsGranted(prerequisiteScope, grantedAtUtc, maximumEvidenceAge))
            {
                throw new InvalidOperationException(
                    $"Cannot grant {scope} because the prerequisite {prerequisiteScope} is not currently granted.");
            }
        }

        _grants[scope] = new InstrumentEligibilityGrant(scope, grantedAtUtc, evidenceAsOfUtc, approvedByUserId);
    }

    /// <summary>
    /// Revokes a purpose and every purpose that depends on it, for the same
    /// interval and product type.
    /// </summary>
    /// <returns>Every scope that was revoked, including the one requested.</returns>
    public IReadOnlyCollection<EligibilityScope> Revoke(EligibilityScope scope)
    {
        var revoked = new List<EligibilityScope>();

        foreach (var candidate in _grants.Keys.ToList())
        {
            if (candidate.Interval != scope.Interval || candidate.ProductType != scope.ProductType)
            {
                continue;
            }

            if (candidate.Purpose == scope.Purpose || DependsOn(candidate.Purpose, scope.Purpose))
            {
                _grants.Remove(candidate);
                revoked.Add(candidate);
            }
        }

        return revoked;
    }

    /// <summary>
    /// Revokes every grant, for example when the instrument is suspended.
    /// </summary>
    public IReadOnlyCollection<EligibilityScope> RevokeAll()
    {
        var revoked = _grants.Keys.ToList();
        _grants.Clear();
        return revoked;
    }

    /// <summary>
    /// True when a grant record exists for the scope, regardless of whether
    /// its evidence has since expired. Use this to tell "there was something
    /// to withdraw" from "it was already absent"; use
    /// <see cref="IsGranted"/> to decide whether the scope may be used.
    /// </summary>
    public bool HasGrant(EligibilityScope scope) => _grants.ContainsKey(scope);

    /// <summary>
    /// True when the scope is granted, the grant's evidence has not expired,
    /// and every prerequisite is itself currently granted and unexpired.
    /// </summary>
    public bool IsGranted(EligibilityScope scope, DateTimeOffset nowUtc, TimeSpan maximumEvidenceAge)
    {
        if (!_grants.TryGetValue(scope, out var grant) || grant.IsExpired(nowUtc, maximumEvidenceAge))
        {
            return false;
        }

        var prerequisite = Prerequisite(scope.Purpose);
        return prerequisite is null
            || IsGranted(scope with { Purpose = prerequisite.Value }, nowUtc, maximumEvidenceAge);
    }

    /// <summary>
    /// Removes every grant whose evidence has expired, cascading to dependent
    /// purposes. Called by the recalculation worker so that a failure to
    /// refresh evidence degrades eligibility instead of preserving it.
    /// </summary>
    public IReadOnlyCollection<EligibilityScope> ExpireStale(DateTimeOffset nowUtc, TimeSpan maximumEvidenceAge)
    {
        var expired = _grants.Values
            .Where(grant => grant.IsExpired(nowUtc, maximumEvidenceAge))
            .Select(grant => grant.Scope)
            .ToList();

        var revoked = new HashSet<EligibilityScope>();
        foreach (var scope in expired)
        {
            foreach (var cascaded in Revoke(scope))
            {
                revoked.Add(cascaded);
            }
        }

        return revoked;
    }

    private static bool DependsOn(EligibilityPurpose candidate, EligibilityPurpose ancestor)
    {
        var current = Prerequisite(candidate);
        while (current is not null)
        {
            if (current.Value == ancestor)
            {
                return true;
            }

            current = Prerequisite(current.Value);
        }

        return false;
    }
}
