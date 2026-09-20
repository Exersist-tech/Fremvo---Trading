using System.Collections.Concurrent;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;

namespace Trading.Application.Pipeline;

/// <summary>
/// Append-only, in-process audit sink for paper and experiment hosts that have no database
/// configured. It is explicitly NOT durable and must never be registered for live trading, where
/// audit events are required to survive a process restart.
/// </summary>
public sealed class InMemoryAuditEventWriter : IAuditEventWriter
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public IReadOnlyCollection<AuditEvent> Events => _events.ToArray();

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();

        _events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }
}
