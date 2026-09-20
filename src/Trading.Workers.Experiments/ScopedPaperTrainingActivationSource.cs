using Trading.Application.Experiments;

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
        var source = scope.ServiceProvider.GetRequiredService<IPaperTrainingActivationSource>();
        return await source.GetActiveOwnerIdsAsync(cancellationToken).ConfigureAwait(false);
    }
}
