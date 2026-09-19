using Trading.Domain.Audit;

namespace Trading.Application.UseCases.Audit;

public interface IAuditQueryService
{
    Task<IReadOnlyList<AuditEvent>> ListAsync(int skip, int take, CancellationToken cancellationToken = default);
}

public sealed class AuditQueryService : IAuditQueryService
{
    private readonly IQueryable<AuditEvent> _source;

    public AuditQueryService(IQueryable<AuditEvent> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public Task<IReadOnlyList<AuditEvent>> ListAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        return Task.FromResult<IReadOnlyList<AuditEvent>>(
            _source
                .OrderByDescending(auditEvent => auditEvent.OccurredAtUtc)
                .Skip(skip)
                .Take(take)
                .ToList());
    }
}
