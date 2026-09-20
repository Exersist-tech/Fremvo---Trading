using Microsoft.EntityFrameworkCore;
using Trading.Application.UseCases.Identity;
using Trading.Domain.Identity;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;

namespace Trading.Web.Development;

/// <summary>
/// Creates the local schema and a small set of demo accounts so the
/// application can be explored end to end on a developer machine.
/// </summary>
/// <remarks>
/// <para>
/// This is a development convenience and is deliberately hostile to being
/// used anywhere else. It refuses to run unless the hosting environment is
/// Development <em>and</em> the operator has explicitly opted in with
/// <c>Development:SeedDemoData</c>. If that flag is ever set outside
/// Development the application fails to start rather than quietly seeding
/// known accounts into a real environment.
/// </para>
/// <para>
/// It grants nothing that weakens the security model. Sign-in still goes
/// through the normal authentication path, authorization still comes from
/// the user's role claim, and the administrator account is created with
/// multi-factor authentication already enabled because the domain forbids
/// administrator privilege without it. No trading capability is enabled:
/// live trading remains off, and the demo users hold no exchange
/// credentials.
/// </para>
/// <para>
/// <c>EnsureCreatedAsync</c> is used because no migrations exist yet. It is
/// a local bootstrap only and is not a substitute for the migration work
/// each phase still owes.
/// </para>
/// </remarks>
internal static class DevelopmentDataSeeder
{
    private static readonly Action<ILogger, string, string, string, Exception?> s_seeded =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(1, nameof(SeedAsync)),
            "Development demo data seeded. Administrator {Administrator}, trader {Trader}, invitation code {InvitationCode}.");

    private static readonly Action<ILogger, string, Exception?> s_rebuilding =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2, "RebuildingDevelopmentDatabase"),
            "The development database is missing tables or columns ({MissingSchema}) because EnsureCreated does not " +
            "alter an existing database. Rebuilding it and reseeding demo data. This path is development only.");
    /// <summary>
    /// The demo administrator's address. The password is hashed like any
    /// other and stored only as a verifier; the plaintext below exists solely
    /// so a developer can sign in to a local instance.
    /// </summary>
    internal const string AdministratorEmail = "admin@fremvo.local";

    internal const string TraderEmail = "trader@fremvo.local";

    /// <summary>
    /// The password given to both demo accounts.
    /// </summary>
    /// <remarks>
    /// A known constant is acceptable only because this seeder refuses to run
    /// outside Development and only when explicitly opted in. It is hashed
    /// with the same hasher used everywhere else, so the demo accounts
    /// exercise the real authentication path rather than a bypass.
    /// </remarks>
    internal const string DemoPassword = "DemoPassword123!";

    internal const string InvitationCode = "FREMVO-DEMO-INVITE";

    public static async Task SeedAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var enabled = app.Configuration.GetValue<bool>("Development:SeedDemoData");
        if (!enabled)
        {
            return;
        }

        if (!app.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Development:SeedDemoData is enabled outside the Development environment. " +
                "Demo accounts must never be created in a non-development environment.");
        }

        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        // EnsureCreated decides at the database level, not the table level, so a
        // database left over from an earlier build keeps its old schema and
        // silently lacks any table added since. That surfaces later as
        // "Invalid object name", far from the cause. Because this is local demo
        // data only, an incomplete development database is rebuilt instead.
        //
        // This is a direct consequence of having no migrations yet and is not a
        // pattern any deployed environment may use.
        var missingTables = await FindMissingSchemaAsync(dbContext, cancellationToken).ConfigureAwait(false);
        if (missingTables.Count > 0)
        {
            s_rebuilding(app.Logger, string.Join(", ", missingTables), null);

            await dbContext.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await dbContext.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Multi-factor authentication is enabled on creation because the
        // domain refuses administrator privilege without it. Seeding an
        // administrator with it disabled would create an account that cannot
        // perform the actions it appears to have.
        var administrator = User.CreateWithMfa(
            Guid.NewGuid(),
            AdministratorEmail,
            "Demo Administrator",
            "en-US",
            "UTC",
            "USD",
            RoleType.Administrator,
            multiFactorAuthenticationEnabled: true,
            UserStatus.Active,
            // Hashed separately from the trader's, so the two rows carry
            // different salts exactly as two real users choosing the same
            // password would.
            passwordHash: passwordHasher.Hash(DemoPassword));

        var trader = User.CreateWithMfa(
            Guid.NewGuid(),
            TraderEmail,
            "Demo Trader",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            multiFactorAuthenticationEnabled: false,
            UserStatus.Active,
            passwordHash: passwordHasher.Hash(DemoPassword));

        dbContext.Users.AddRange(administrator, trader);

        // An unused invitation so the invitation-only registration flow can
        // be exercised without first signing in as an administrator.
        dbContext.Invitations.Add(new Invitation(
            Guid.NewGuid(),
            InvitationCode,
            administrator.Id,
            maxUses: 25,
            usedCount: 0,
            expiresAtUtc: DateTimeOffset.UtcNow.AddYears(1),
            isActive: true));

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        s_seeded(app.Logger, AdministratorEmail, TraderEmail, InvitationCode, null);
    }

    /// <summary>
    /// Returns the tables and columns the model expects but the database does
    /// not have.
    /// </summary>
    /// <remarks>
    /// Columns are checked as well as tables because a schema change that adds
    /// a column to an existing table leaves the table present and the column
    /// absent. <c>EnsureCreated</c> does nothing in that case, and the first
    /// symptom is "Invalid column name" from a query far away from the change.
    /// </remarks>
    private static async Task<IReadOnlyList<string>> FindMissingSchemaAsync(
        TradingDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existingTables = new HashSet<string>(
            await dbContext.Database
                .SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        var existingColumns = new HashSet<string>(
            await dbContext.Database
                .SqlQueryRaw<string>(
                    "SELECT t.name + '.' + c.name AS Value " +
                    "FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();

        foreach (var entityType in dbContext.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (string.IsNullOrEmpty(table))
            {
                continue;
            }

            if (!existingTables.Contains(table))
            {
                if (!missing.Contains(table, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(table);
                }

                // No point listing every column of a table that is absent.
                continue;
            }

            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName();
                if (string.IsNullOrEmpty(column))
                {
                    continue;
                }

                var qualified = $"{table}.{column}";
                if (!existingColumns.Contains(qualified)
                    && !missing.Contains(qualified, StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add(qualified);
                }
            }
        }

        return missing;
    }
}
