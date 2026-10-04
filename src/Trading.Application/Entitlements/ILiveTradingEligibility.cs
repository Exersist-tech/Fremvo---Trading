namespace Trading.Application.Entitlements;

public interface ILiveTradingEligibility
{
    Task<bool> IsEligibleAsync(Guid ownerId, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

public sealed class EntitlementLiveTradingEligibility(IEntitlementRepository entitlements)
    : ILiveTradingEligibility
{
    public async Task<bool> IsEligibleAsync(Guid ownerId, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || asOfUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An owner and UTC instant are required for live eligibility.");
        var entitlement = await entitlements.GetEffectiveAsync(ownerId, asOfUtc, cancellationToken)
            .ConfigureAwait(false);
        if (entitlement is null)
            return false;
        var plan = await entitlements.GetPlanAsync(entitlement.PlanId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("An active live entitlement references a missing plan.");
        return plan.LiveTradingEligible;
    }
}
