using Trading.Application.Entitlements;

namespace Trading.Application.Experiments;

public interface IPaperWorkerAdmissionLimit
{
    Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}

public sealed class EntitlementPaperWorkerAdmissionLimit(
    IEntitlementRepository entitlements, bool enforcementEnabled)
    : IPaperWorkerAdmissionLimit
{
    public async Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || asOfUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An owner and UTC instant are required for paper worker admission.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!await entitlements.IsOwnerActiveAsync(ownerId, cancellationToken).ConfigureAwait(false))
            return 0;
        if (!enforcementEnabled)
            return PaperTrainingActivationService.MaximumSlots;
        var entitlement = await entitlements.GetEffectiveAsync(ownerId, asOfUtc, cancellationToken)
            .ConfigureAwait(false);
        if (entitlement is null)
            return 0;
        var plan = await entitlements.GetPlanAsync(entitlement.PlanId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("An active paper entitlement references a missing plan.");
        return plan.MaxExperimentWorkers;
    }
}

public sealed class LegacyPaperWorkerAdmissionLimit : IPaperWorkerAdmissionLimit
{
    public Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ownerId == Guid.Empty || asOfUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An owner and UTC instant are required for paper worker admission.");
        return Task.FromResult(PaperTrainingActivationService.MaximumSlots);
    }
}
