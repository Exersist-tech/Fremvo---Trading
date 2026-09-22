using Trading.Application.Experiments;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.Workers.Experiments;

/// <summary>Reads durable activation state in a short-lived scope on each host tick.</summary>
public sealed class ScopedPaperTrainingActivationSource : IPaperTrainingActivationSource
{
    private readonly IServiceScopeFactory _scopes;

    public ScopedPaperTrainingActivationSource(IServiceScopeFactory scopes) =>
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    public async Task<IReadOnlyCollection<Guid>> GetActiveOwnerIdsAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<EfPaperTrainingActivationRepository>();
        return await source.GetActiveOwnerIdsAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ScopedPaperTrainingActivationReader : IPaperTrainingActivationReader
{
    private readonly IServiceScopeFactory _scopes;

    public ScopedPaperTrainingActivationReader(IServiceScopeFactory scopes) =>
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    public async Task<PaperTrainingActivation?> GetAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPaperTrainingActivationRepository>()
            .GetAsync(ownerId, cancellationToken)
            .ConfigureAwait(false);
    }
}
