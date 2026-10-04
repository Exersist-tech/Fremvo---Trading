using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Trading.Domain.Audit;
using Trading.Domain.Positions;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;

namespace Trading.ArchitectureTests;

public sealed class TradingMigrationIntegrationTests
{
    [Fact]
    public async Task ExistingLivePositionsRemainExplicitlyUnboundAfterAccountBindingMigration()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingPositionBinding_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var strategy = Guid.NewGuid();
        var openedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        try
        {
            await using (var db = Context())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261003195746_HashInvitationCodes");
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [Positions]
                    ([Id], [UserId], [StrategyId], [Symbol], [Direction],
                     [Quantity], [EntryPrice], [MarkPrice], [OpenedAtUtc],
                     [Mode], [UnrealizedPnl], [Status], [Version])
                    VALUES ({id}, {owner}, {strategy}, {"XBTUSD"}, {0},
                            {1m}, {100m}, {100m}, {openedAt},
                            {"Live"}, {0m}, {1}, {0})
                    """);
                await db.Database.MigrateAsync();
            }

            await using var migrated = Context();
            var position = await migrated.Positions.AsNoTracking().SingleAsync(value => value.Id == id);
            Assert.Equal(Trading.Domain.Execution.TradingMode.Live, position.Mode);
            Assert.Equal(PositionStatus.Open, position.Status);
            Assert.Null(position.ExchangeAccountId);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SchemaAdoptionPreflightIsReadOnlyAndRejectsCatalogDrift()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var referenceConnection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingSchemaReference_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        var targetConnection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingSchemaTarget_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        TradingDbContext Context(string connection) => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);

        try
        {
            await using (var reference = Context(referenceConnection))
                await reference.Database.MigrateAsync();
            var auditId = Guid.NewGuid();
            await using (var target = Context(targetConnection))
            {
                Assert.True(await target.Database.EnsureCreatedAsync());
                target.AuditEvents.Add(new AuditEvent(auditId, null, "Test.SchemaProof",
                    "DisposableDatabase", "target", DateTimeOffset.UtcNow, null, null, "schema-test"));
                await target.SaveChangesAsync();
            }

            await using (var target = Context(targetConnection))
            await using (var reference = Context(referenceConnection))
            {
                var comparison = await TradingSqlSchemaComparer.CompareAsync(target, reference);
                Assert.True(comparison.Matches,
                    $"Missing={string.Join(",", comparison.MissingFromTarget)}; Unexpected={string.Join(",", comparison.UnexpectedInTarget)}");
                Assert.Empty(await target.Database.GetAppliedMigrationsAsync());
                Assert.NotNull(await target.AuditEvents.FindAsync(auditId));

                await target.Database.ExecuteSqlRawAsync(
                    "CREATE INDEX [IX_SchemaDrift] ON [AuditEvents] ([TargetId])");
                comparison = await TradingSqlSchemaComparer.CompareAsync(target, reference);
                Assert.False(comparison.Matches);
                Assert.NotEmpty(comparison.UnexpectedInTarget);
                Assert.Empty(await target.Database.GetAppliedMigrationsAsync());
                Assert.NotNull(await target.AuditEvents.FindAsync(auditId));
            }
        }
        finally
        {
            await using var reference = Context(referenceConnection);
            await reference.Database.EnsureDeletedAsync();
            await using var target = Context(targetConnection);
            await target.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ExistingEmailBoundInvitationsAreHashedWithoutLosingRedemption()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingInvitationDigest_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var invitationId = Guid.NewGuid();
        var invalidId = Guid.NewGuid();
        const string code = "migrated-invite";
        try
        {
            await using (var db = Context())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261003193341_BindInvitationRecipients");
                db.Invitations.AddRange(
                    new Invitation(invitationId, InvitationCodeDigest.Compute(code),
                        "recipient@example.test", Guid.NewGuid(), 1, 0,
                        DateTimeOffset.UtcNow.AddDays(1), true),
                    new Invitation(invalidId, InvitationCodeDigest.Compute("INVALID-LEGACY"),
                        "other@example.test", Guid.NewGuid(), 1, 0,
                        DateTimeOffset.UtcNow.AddDays(1), true));
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [Invitations] SET [Code] = {code} WHERE [Id] = {invitationId}");
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE [Invitations] SET [Code] = {"BAD-\u00c9"} WHERE [Id] = {invalidId}");
                await db.Database.MigrateAsync();
            }

            await using (var db = Context())
            {
                var invitation = await db.Invitations.AsNoTracking().SingleAsync(item => item.Id == invitationId);
                Assert.Equal(InvitationCodeDigest.Compute(code), invitation.Code);
                Assert.True(invitation.IsUsableAt(DateTimeOffset.UtcNow));
                Assert.Equal(invitationId, (await db.Invitations.AsNoTracking()
                    .SingleAsync(item => item.Code == InvitationCodeDigest.Compute(code))).Id);
                var invalid = await db.Invitations.AsNoTracking().SingleAsync(item => item.Id == invalidId);
                Assert.False(invalid.IsActive);
            }
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task BaselineMigrationProvisionsFreshSqlWithoutApplicationBootstrap()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingMigration_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);

        try
        {
            await using (var db = Context())
            {
                Assert.False(await db.Database.CanConnectAsync());
                Assert.False(db.Database.HasPendingModelChanges());
                await db.Database.MigrateAsync();
                Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Empty(await db.Users.ToListAsync());
                Assert.Empty(await db.ExperimentPaperExecutionAssociations.ToListAsync());
                Assert.Empty(await db.Plans.ToListAsync());
                Assert.Empty(await db.Entitlements.ToListAsync());
                await db.Database.MigrateAsync();
                Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
            }

            using var services = new ServiceCollection()
                .AddDbContext<TradingDbContext>(options => options.UseSqlServer(connection))
                .BuildServiceProvider();
            var readiness = new PaperHostSchemaReadiness(
                services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
                NullLogger<PaperHostSchemaReadiness>.Instance, TimeSpan.FromSeconds(5));
            await readiness.StartAsync(CancellationToken.None);
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
