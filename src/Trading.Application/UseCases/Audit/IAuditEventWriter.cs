using Trading.Domain.Audit;

namespace Trading.Application.UseCases.Audit;

public interface IAuditEventWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
