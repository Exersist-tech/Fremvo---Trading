using Trading.Domain.Entitlements;

namespace Trading.Application.Entitlements;

public interface IEntitlementRepository
{
    Task<bool> IsOwnerActiveAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<Plan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default);
    Task<Entitlement?> GetEffectiveAsync(
        Guid userId, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default);
    Task AddPlanAsync(Plan plan, Guid administratorId, CancellationToken cancellationToken = default);
    Task AssignAsync(Entitlement entitlement, Guid administratorId, CancellationToken cancellationToken = default);
    Task RevokeAsync(Guid userId, Guid entitlementId, Guid administratorId,
        CancellationToken cancellationToken = default);
    Task ExtendTrialAsync(Guid userId, Guid entitlementId, DateTimeOffset newExpiryUtc,
        Guid administratorId, CancellationToken cancellationToken = default);
}
