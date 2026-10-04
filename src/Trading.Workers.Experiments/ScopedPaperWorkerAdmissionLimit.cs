using Trading.Application.Entitlements;
using Trading.Application.Experiments;

namespace Trading.Workers.Experiments;

public sealed class ScopedPaperWorkerAdmissionLimit(IServiceScopeFactory scopes, bool enforcementEnabled)
    : IPaperWorkerAdmissionLimit
{
    public async Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var policy = new EntitlementPaperWorkerAdmissionLimit(
            scope.ServiceProvider.GetRequiredService<IEntitlementRepository>(), enforcementEnabled);
        return await policy.GetMaximumAsync(ownerId, asOfUtc, cancellationToken).ConfigureAwait(false);
    }
}
