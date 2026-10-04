using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Trading.Domain.Entitlements;
using Trading.Domain.Identity;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Entitlements;
using Trading.Application.Entitlements;
using Trading.Application.Experiments;

namespace Trading.ArchitectureTests;

public sealed class EntitlementRepositoryTests
{
    [Fact]
    public async Task LiveEligibilityRequiresAnActiveOwnerAndAnExplicitLivePlan()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var expiry = start.AddHours(1);
        var paper = new Plan(Guid.NewGuid(), "PAPER", "Paper", 3);
        var live = new Plan(Guid.NewGuid(), "LIVE", "Live eligible", 1, liveTradingEligible: true);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Users.Add(new User(owner, "live-policy@example.test", "Live Policy",
            "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
        db.Users.Add(new User(other, "other-live-policy@example.test", "Other Policy",
            "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
        db.Plans.AddRange(paper, live);
        var assignment = new Entitlement(Guid.NewGuid(), owner, paper.Id, start, expiry);
        db.Entitlements.Add(assignment);
        await db.SaveChangesAsync();
        var eligibility = new EntitlementLiveTradingEligibility(
            new EfEntitlementRepository(db, TimeProvider.System));

        Assert.False(await eligibility.IsEligibleAsync(owner, start));
        Assert.False(await eligibility.IsEligibleAsync(other, start));
        assignment.Revoke();
        db.Entitlements.Add(new Entitlement(Guid.NewGuid(), owner, live.Id, start, expiry));
        await db.SaveChangesAsync();
        Assert.True(await eligibility.IsEligibleAsync(owner, expiry.AddTicks(-1)));
        Assert.False(await eligibility.IsEligibleAsync(owner, expiry));
        db.Entry(db.Users.Single(user => user.Id == owner))
            .Property(user => user.Status).CurrentValue = UserStatus.Suspended;
        await db.SaveChangesAsync();
        Assert.False(await eligibility.IsEligibleAsync(owner, start));
    }

    [Fact]
    public async Task PaperWorkerLimitIsOwnerScopedAndExpiresExactlyAtTheTrialBoundary()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var expiry = start.AddDays(1);
        var plan = new Plan(Guid.NewGuid(), "PAPER-ONE", "One paper worker", 1);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        db.Users.Add(new User(owner, "policy-owner@example.test", "Policy Owner",
            "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
        db.Users.Add(new User(other, "policy-other@example.test", "Other Owner",
            "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
        db.Plans.Add(plan);
        db.Entitlements.Add(new Entitlement(Guid.NewGuid(), owner, plan.Id, start, expiry));
        await db.SaveChangesAsync();
        var repository = new EfEntitlementRepository(db, TimeProvider.System);
        var enabled = new EntitlementPaperWorkerAdmissionLimit(repository, true);

        Assert.Equal(1, await enabled.GetMaximumAsync(owner, expiry.AddTicks(-1)));
        Assert.Equal(0, await enabled.GetMaximumAsync(owner, expiry));
        Assert.Equal(0, await enabled.GetMaximumAsync(other, start));
        Assert.Equal(PaperTrainingActivationService.MaximumSlots,
            await new EntitlementPaperWorkerAdmissionLimit(repository, false)
                .GetMaximumAsync(other, expiry));
        db.Entry(db.Users.Single(user => user.Id == other))
            .Property(user => user.Status).CurrentValue = UserStatus.Suspended;
        await db.SaveChangesAsync();
        Assert.Equal(0, await new EntitlementPaperWorkerAdmissionLimit(repository, false)
            .GetMaximumAsync(other, expiry));
        await Assert.ThrowsAsync<ArgumentException>(() => enabled.GetMaximumAsync(
            owner, start.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void PlansAndTrialsValidateLimitsAndExactUtcExpiry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Plan(Guid.NewGuid(), "UNSAFE", "Unsafe", 11));
        Assert.Throws<ArgumentException>(() =>
            new Plan(Guid.NewGuid(), "unsafe", "Unsafe", 1));
        Assert.Throws<ArgumentException>(() =>
            new Plan(Guid.NewGuid(), "FUTURES", "Futures", 1, futuresEligible: true));
        var start = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var entitlement = new Entitlement(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            start, start.AddDays(7));
        Assert.False(entitlement.IsEffectiveAt(start.AddTicks(-1)));
        Assert.True(entitlement.IsEffectiveAt(start));
        Assert.True(entitlement.IsEffectiveAt(start.AddDays(7).AddTicks(-1)));
        Assert.False(entitlement.IsEffectiveAt(start.AddDays(7)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            entitlement.ExtendTrial(start, start.AddDays(7)));
        Assert.Throws<ArgumentException>(() =>
            entitlement.ExtendTrial(start, start.AddDays(9).ToOffset(TimeSpan.FromHours(1))));
        entitlement.ExtendTrial(start.AddDays(8), start.AddDays(10));
        Assert.True(entitlement.IsEffectiveAt(start.AddDays(8)));
        Assert.False(entitlement.IsEffectiveAt(start.AddDays(10)));
        Assert.Throws<ArgumentException>(() => entitlement.IsEffectiveAt(start.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => new Entitlement(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), start.ToOffset(TimeSpan.FromHours(1))));
        entitlement.Revoke();
        Assert.False(entitlement.IsEffectiveAt(start));
        Assert.Throws<InvalidOperationException>(entitlement.Revoke);
        Assert.Throws<InvalidOperationException>(() =>
            entitlement.ExtendTrial(start, start.AddDays(12)));
    }

    [Fact]
    public async Task PlansAndOwnerEntitlementsPersistWithAuditAndFailClosedOnTrialExpiry()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingEntitlements_{Guid.NewGuid():N}",
            IntegratedSecurity = true, Encrypt = false, TrustServerCertificate = true
        }.ConnectionString;
        TradingDbContext Context() => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlServer(connection).Options);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var plan = new Plan(Guid.NewGuid(), "PAPER-ONE", "One paper worker", 1);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var expiry = start.AddDays(7);
        var granted = new Entitlement(Guid.NewGuid(), owner, plan.Id, start, expiry);
        try
        {
            await using (var db = Context())
            {
                Assert.True(await db.Database.EnsureCreatedAsync());
                db.Users.Add(new User(owner, "entitled@example.test", "Entitled",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
                db.Users.Add(new User(other, "other-entitlement@example.test", "Other",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
                db.Users.Add(new User(admin, "entitlement-admin@example.test", "Admin",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active));
                await db.SaveChangesAsync();
                var repository = new EfEntitlementRepository(db, TimeProvider.System);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    repository.AddPlanAsync(plan, other));
                await repository.AddPlanAsync(plan, admin);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    repository.AssignAsync(granted, other));
                await repository.AssignAsync(granted, admin);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    repository.AssignAsync(new Entitlement(Guid.NewGuid(), owner, plan.Id, start), admin));
            }
            await using (var db = Context())
            {
                db.Entitlements.Add(new Entitlement(Guid.NewGuid(), owner, plan.Id, start));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            }
            await using (var db = Context())
            {
                var repository = new EfEntitlementRepository(db, TimeProvider.System);
                Assert.Equal(1, (await repository.GetPlanAsync(plan.Id))!.MaxExperimentWorkers);
                Assert.Equal(granted.Id, (await repository.GetEffectiveAsync(owner, expiry.AddTicks(-1)))!.Id);
                Assert.Null(await repository.GetEffectiveAsync(owner, expiry));
                Assert.Null(await repository.GetEffectiveAsync(other, start));
                var limit = new EntitlementPaperWorkerAdmissionLimit(repository, true);
                Assert.Equal(1, await limit.GetMaximumAsync(owner, expiry.AddTicks(-1)));
                Assert.Equal(0, await limit.GetMaximumAsync(owner, expiry));
                Assert.Equal(0, await limit.GetMaximumAsync(other, start));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    repository.ExtendTrialAsync(owner, granted.Id, expiry.AddDays(7), other));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    repository.ExtendTrialAsync(other, granted.Id, expiry.AddDays(7), admin));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                    repository.ExtendTrialAsync(owner, granted.Id, expiry, admin));
                await repository.ExtendTrialAsync(owner, granted.Id, expiry.AddDays(7), admin);
                Assert.NotNull(await repository.GetEffectiveAsync(owner, expiry));
                Assert.Equal(1, await limit.GetMaximumAsync(owner, expiry));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                    repository.ExtendTrialAsync(owner, granted.Id, expiry.AddDays(7), admin));
                Assert.Equal(PaperTrainingActivationService.MaximumSlots,
                    await new EntitlementPaperWorkerAdmissionLimit(repository, false)
                        .GetMaximumAsync(other, start));
                var livePlan = new Plan(Guid.NewGuid(), "LIVE-TEST", "Live eligibility", 1,
                    liveTradingEligible: true);
                db.Plans.Add(livePlan);
                db.Entitlements.Add(new Entitlement(Guid.NewGuid(), other, livePlan.Id, start, expiry));
                await db.SaveChangesAsync();
                var liveEligibility = new EntitlementLiveTradingEligibility(repository);
                Assert.True(await liveEligibility.IsEligibleAsync(other, expiry.AddTicks(-1)));
                Assert.False(await liveEligibility.IsEligibleAsync(other, expiry));
                Assert.False(await liveEligibility.IsEligibleAsync(owner, start));
                Assert.Equal(1, await db.Users.Where(user => user.Id == other)
                    .ExecuteUpdateAsync(update => update.SetProperty(user => user.Status, UserStatus.Suspended)));
                Assert.False(await liveEligibility.IsEligibleAsync(other, start));
                Assert.Equal(0, await new EntitlementPaperWorkerAdmissionLimit(repository, false)
                    .GetMaximumAsync(other, start));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    repository.RevokeAsync(other, granted.Id, admin));
                Assert.Equal(2, await db.Entitlements.CountAsync(value => value.Status == EntitlementStatus.Active));
                await repository.RevokeAsync(owner, granted.Id, admin);
                Assert.Null(await repository.GetEffectiveAsync(owner, start));
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    repository.RevokeAsync(owner, granted.Id, admin));
                await repository.AssignAsync(new Entitlement(Guid.NewGuid(), owner, plan.Id,
                    start, null), admin);
                Assert.NotNull(await repository.GetEffectiveAsync(owner, expiry.AddYears(1)));
                var audits = await db.AuditEvents.AsNoTracking()
                    .Where(value => value.Action.StartsWith("Entitlements.")).ToListAsync();
                Assert.Equal(5, audits.Count);
                Assert.All(audits, value => Assert.Equal(admin, value.ActorUserId));
            }
        }
        finally
        {
            await using var db = Context();
            await db.Database.EnsureDeletedAsync();
        }
    }
}
