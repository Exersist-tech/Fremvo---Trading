using Microsoft.EntityFrameworkCore;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;

namespace Trading.Infrastructure.Data.Audit;

public sealed class EfAuditEventWriter : IAuditEventWriter
{
    private readonly TradingDbContext _dbContext;

    public EfAuditEventWriter(TradingDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        _dbContext.AuditEvents.Add(auditEvent);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
