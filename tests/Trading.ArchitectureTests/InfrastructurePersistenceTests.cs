using Microsoft.EntityFrameworkCore;
using Trading.Domain.Audit;
using Trading.Domain.Identity;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;

#nullable disable

namespace Trading.ArchitectureTests;

public sealed class InfrastructurePersistenceTests
{
    [Fact]
    public void TradingDbContextMapsIdentityEntitiesAndConstraints()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new TradingDbContext(options);

        var userEntity = context.Model.FindEntityType(typeof(User));
        Assert.NotNull(userEntity);
        Assert.Equal("Users", userEntity!.GetTableName());
        Assert.Equal(254, userEntity.FindProperty(nameof(User.Email))!.GetMaxLength());
        Assert.Equal(3, userEntity.FindProperty(nameof(User.ReportingCurrency))!.GetMaxLength());
        Assert.Contains(userEntity.GetIndexes(), index => index.Properties.Select(property => property.Name).Contains(nameof(User.Email)));

        var invitationEntity = context.Model.FindEntityType(typeof(Invitation));
        Assert.NotNull(invitationEntity);
        Assert.Equal("Invitations", invitationEntity!.GetTableName());
        Assert.Contains(invitationEntity.GetIndexes(), index => index.Properties.Select(property => property.Name).Contains(nameof(Invitation.Code)));

        var auditEntity = context.Model.FindEntityType(typeof(AuditEvent));
        Assert.NotNull(auditEntity);
        Assert.Equal("AuditEvents", auditEntity!.GetTableName());
        Assert.True(auditEntity.FindProperty(nameof(AuditEvent.CorrelationId))!.IsNullable == false);
    }

    [Fact]
    public void TradingDbContextPersistsUserAndAuditEventRows()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new TradingDbContext(options);

        var user = new User(
            Guid.NewGuid(),
            "user@example.com",
            "Example User",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            true,
            UserStatus.Active);

        context.Users.Add(user);
        context.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            user.Id,
            "User.Registered",
            "User",
            user.Id.ToString(),
            DateTimeOffset.UtcNow,
            null,
            null,
            "corr-100"));

        context.SaveChanges();

        Assert.Equal(1, context.Users.Count());
        Assert.Equal(1, context.AuditEvents.Count());
    }
}
