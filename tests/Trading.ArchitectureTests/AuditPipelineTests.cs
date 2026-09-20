using Microsoft.EntityFrameworkCore;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Audit;

namespace Trading.ArchitectureTests;

public sealed class AuditPipelineTests
{
    [Fact]
    public async Task AuditWriterPersistsEventAndQueryServiceReturnsMostRecentFirstAsync()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new TradingDbContext(options);
        var writer = new EfAuditEventWriter(context);
        var oldEvent = new AuditEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "User.LoggedIn",
            "User",
            "old-user",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            null,
            null,
            "corr-1");
        var newEvent = new AuditEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "User.Registered",
            "User",
            "new-user",
            DateTimeOffset.UtcNow,
            null,
            null,
            "corr-2");

        await writer.WriteAsync(oldEvent);
        await writer.WriteAsync(newEvent);

        var query = new AuditQueryService(context.AuditEvents);
        var results = await query.ListAsync(0, 10);

        Assert.Equal(2, results.Count);
        Assert.Equal(newEvent.Id, results[0].Id);
        Assert.Equal(oldEvent.Id, results[1].Id);
    }
}
