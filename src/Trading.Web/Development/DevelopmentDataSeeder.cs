using Microsoft.EntityFrameworkCore;
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
    /// <summary>
    /// The demo administrator's address. No password is stored here or
    /// anywhere else: the current authentication service accepts any
    /// password of sufficient length for an active user, which is a known
    /// gap tracked for the credential-hardening task.
    /// </summary>
    internal const string AdministratorEmail = "admin@fremvo.local";

    internal const string TraderEmail = "trader@fremvo.local";

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

        await dbContext.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

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
            UserStatus.Active);

        var trader = User.CreateWithMfa(
            Guid.NewGuid(),
            TraderEmail,
            "Demo Trader",
            "en-US",
            "UTC",
            "USD",
            RoleType.User,
            multiFactorAuthenticationEnabled: false,
            UserStatus.Active);

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
}
