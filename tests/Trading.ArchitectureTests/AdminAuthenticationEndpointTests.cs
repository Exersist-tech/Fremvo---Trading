extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Trading.Application.UseCases.Identity;
using Trading.Application.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Entitlements;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Entitlements;
using Trading.Infrastructure.Secrets;

namespace Trading.ArchitectureTests;

public sealed class AdminAuthenticationEndpointTests
{
    [Fact]
    public async Task AdministratorPromotionRequiresTargetMfaProofAndFreshApproverMfa()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingAdminPromotion_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        var secrets = new InMemorySecretStore();
        var clock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59));
        await using var factory = new AdminWebApplicationFactory(connection, secrets: secrets, clock: clock);
        var administratorId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var unprovedId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(administratorId, "promote-admin@example.test", "Admin",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    hasher.Hash("StrongAdminPassword123!")));
                db.Users.Add(new User(candidateId, "candidate@example.test", "Candidate",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongCandidatePassword123!")));
                db.Users.Add(new User(unprovedId, "unproved@example.test", "Unproved",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongCandidatePassword123!")));
                await db.SaveChangesAsync();
            }
            const string testOnlySecret =
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA";
            await secrets.StoreSecretAsync($"admin-mfa-{administratorId:N}", testOnlySecret);
            await secrets.StoreSecretAsync($"admin-mfa-{candidateId:N}", testOnlySecret);
            using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var candidate = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var adminLogin = await admin.PostAsJsonAsync("/api/login",
                new { email = "promote-admin@example.test", password = "StrongAdminPassword123!",
                    oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
            using var candidateLogin = await candidate.PostAsJsonAsync("/api/login",
                new { email = "candidate@example.test", password = "StrongCandidatePassword123!" });
            Assert.Equal(HttpStatusCode.OK, candidateLogin.StatusCode);
            using var noAuthority = await candidate.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{candidateId}/administrator",
                new { reason = "Self promotion", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.Forbidden, noAuthority.StatusCode);
            using var noFreshCode = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{candidateId}/administrator",
                new { reason = "Review", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.BadRequest, noFreshCode.StatusCode);
            using var proof = await candidate.PostAsJsonAsync("/api/account/administrator-mfa/prove",
                new { oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(60);
            using var approved = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{candidateId}/administrator",
                new { reason = "Separate MFA verified", oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            using var stale = await candidate.GetAsync(new Uri("/api/reporting/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(90);
            using var promotedLogin = await candidate.PostAsJsonAsync("/api/login",
                new { email = "candidate@example.test", password = "StrongCandidatePassword123!",
                    oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.OK, promotedLogin.StatusCode);
            using var identity = await candidate.GetAsync(new Uri("/api/me", UriKind.Relative));
            Assert.Contains("\"isAdministrator\":true", await identity.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            using var noProof = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{unprovedId}/administrator",
                new { reason = "Cannot skip user proof", oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.Conflict, noProof.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(120);
            using var revoked = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{candidateId}/administrator/revoke",
                new { reason = "Access no longer required", oneTimeCode = "791148" });
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            using var oldAdministratorSession = await candidate.GetAsync(
                new Uri("/api/reporting/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, oldAdministratorSession.StatusCode);
            using var userLogin = await candidate.PostAsJsonAsync("/api/login",
                new { email = "candidate@example.test", password = "StrongCandidatePassword123!" });
            Assert.Equal(HttpStatusCode.OK, userLogin.StatusCode);
            using var selfRevoke = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{administratorId}/administrator/revoke",
                new { reason = "Cannot remove self", oneTimeCode = "744399" });
            Assert.Equal(HttpStatusCode.BadRequest, selfRevoke.StatusCode);
            using var scopeAfter = factory.Services.CreateScope();
            var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TradingDbContext>();
            var promoted = await dbAfter.Users.AsNoTracking().SingleAsync(user => user.Id == candidateId);
            Assert.Equal(RoleType.User, promoted.Role);
            Assert.False(promoted.MultiFactorAuthenticationEnabled);
            Assert.Equal(RoleType.User, (await dbAfter.Users.AsNoTracking()
                .SingleAsync(user => user.Id == unprovedId)).Role);
            Assert.Single(await dbAfter.AuditEvents.Where(item => item.Action == "User.AdministratorGranted")
                .ToListAsync());
            Assert.Single(await dbAfter.AuditEvents.Where(item => item.Action == "User.AdministratorRevoked")
                .ToListAsync());
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task FreshAdminMfaChangesOnlyRiskOfficerRoleAndInvalidatesOldSessions()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingRiskOfficerRole_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        var secrets = new InMemorySecretStore();
        var clock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59));
        await using var factory = new AdminWebApplicationFactory(connection, secrets: secrets, clock: clock);
        var administratorId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(administratorId, "risk-admin@example.test", "Admin",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    hasher.Hash("StrongAdminPassword123!")));
                db.Users.Add(new User(ownerId, "risk-owner@example.test", "Owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongOwnerPassword123!")));
                await db.SaveChangesAsync();
            }
            await secrets.StoreSecretAsync($"admin-mfa-{administratorId:N}",
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA");
            using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var login = await admin.PostAsJsonAsync("/api/login",
                new { email = "risk-admin@example.test", password = "StrongAdminPassword123!",
                    oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var ownerLogin = await owner.PostAsJsonAsync("/api/login",
                new { email = "risk-owner@example.test", password = "StrongOwnerPassword123!" });
            Assert.Equal(HttpStatusCode.OK, ownerLogin.StatusCode);
            using var unauthorized = await owner.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/role",
                new { role = "RiskOfficer", reason = "Unauthorized", oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(60);
            using var replay = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/role",
                new { role = "RiskOfficer", reason = "Review", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            using var forbiddenRole = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/role",
                new { role = "Administrator", reason = "Not permitted", oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.BadRequest, forbiddenRole.StatusCode);
            using var grant = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/role",
                new { role = "RiskOfficer", reason = "Operations coverage", oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
            using var search = await admin.GetAsync(
                new Uri("/api/admin/entitlements/owners/search?query=risk-owner", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            Assert.Contains("\"role\":\"RiskOfficer\"", await search.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            using var oldSession = await owner.GetAsync(new Uri("/api/reporting/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
            using var officerLogin = await owner.PostAsJsonAsync("/api/login",
                new { email = "risk-owner@example.test", password = "StrongOwnerPassword123!" });
            Assert.Equal(HttpStatusCode.OK, officerLogin.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(90);
            using var revoke = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/role",
                new { role = "User", reason = "Coverage ended", oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            using var formerOfficer = await owner.GetAsync(new Uri("/api/reporting/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, formerOfficer.StatusCode);
            using var scopeAfter = factory.Services.CreateScope();
            var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal(RoleType.User, (await dbAfter.Users.AsNoTracking()
                .SingleAsync(user => user.Id == ownerId)).Role);
            Assert.Equal(2, await dbAfter.AuditEvents.CountAsync(item =>
                item.Action == "User.RiskOfficerGranted" || item.Action == "User.RiskOfficerRevoked"));
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task MfaVerifiedOwnerSuspensionRevokesSessionsAndPaperAdmissionButPreservesAccountHistory()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingOwnerStatus_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        var secrets = new InMemorySecretStore();
        var clock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59));
        await using var factory = new AdminWebApplicationFactory(connection, secrets: secrets, clock: clock);
        var administratorId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(administratorId, "status-admin@example.test", "Admin",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    hasher.Hash("StrongAdminPassword123!")));
                db.Users.Add(new User(ownerId, "status-owner@example.test", "Owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongOwnerPassword123!")));
                await db.SaveChangesAsync();
            }
            await secrets.StoreSecretAsync($"admin-mfa-{administratorId:N}",
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA");
            using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var adminLogin = await admin.PostAsJsonAsync("/api/login",
                new { email = "status-admin@example.test", password = "StrongAdminPassword123!",
                    oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
            using var ownerLogin = await owner.PostAsJsonAsync("/api/login",
                new { email = "status-owner@example.test", password = "StrongOwnerPassword123!" });
            Assert.Equal(HttpStatusCode.OK, ownerLogin.StatusCode);
            using var before = await owner.GetAsync(new Uri("/api/reporting/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(60);
            using var invalid = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/status",
                new { status = "Suspended", reason = "Account review", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var suspend = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/status",
                new { status = "Suspended", reason = "Account review", oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
            using var rejected = await owner.GetAsync(new Uri("/api/reporting/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var policy = new EntitlementPaperWorkerAdmissionLimit(
                    new EfEntitlementRepository(db, clock), false);
                Assert.Equal(0, await policy.GetMaximumAsync(ownerId, clock.UtcNow));
                Assert.Equal(UserStatus.Suspended, (await db.Users.AsNoTracking()
                    .SingleAsync(user => user.Id == ownerId)).Status);
                Assert.Single(await db.AuditEvents.Where(item => item.Action == "User.Suspended")
                    .ToListAsync());
            }
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(90);
            using var restore = await admin.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/status",
                new { status = "Active", reason = "Review completed", oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            using var scopeAfter = factory.Services.CreateScope();
            var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal(PaperTrainingActivationService.MaximumSlots,
                await new EntitlementPaperWorkerAdmissionLimit(
                    new EfEntitlementRepository(dbAfter, clock), false)
                    .GetMaximumAsync(ownerId, clock.UtcNow));
            Assert.Equal(2, await dbAfter.AuditEvents.CountAsync(item => item.Action == "User.Suspended"
                || item.Action == "User.Reactivated"));
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task InvitationsNeedFreshMfaAndRevokedCodesCannotBeRedeemed()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingAdminInvitations_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        var secrets = new InMemorySecretStore();
        var clock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59));
        await using var factory = new AdminWebApplicationFactory(connection, secrets: secrets, clock: clock);
        var administratorId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                db.Users.Add(new User(administratorId, "invite-admin@example.test", "Invite Admin",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>()
                        .Hash("StrongAdminPassword123!")));
                await db.SaveChangesAsync();
            }
            await secrets.StoreSecretAsync($"admin-mfa-{administratorId:N}",
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var login = await client.PostAsJsonAsync("/api/login",
                new { email = "invite-admin@example.test", password = "StrongAdminPassword123!",
                    oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(60);
            using var refused = await client.PostAsJsonAsync("/api/invitations",
                new { email = "new@example.test", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            using var issued = await client.PostAsJsonAsync("/api/invitations",
                new { email = "new@example.test", oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
            Assert.Contains("no-store", issued.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            using var document = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());
            Assert.Equal("new@example.test", document.RootElement.GetProperty("recipientEmail").GetString());
            var invitationId = document.RootElement.GetProperty("id").GetGuid();
            var code = document.RootElement.GetProperty("code").GetString();
            Assert.False(string.IsNullOrWhiteSpace(code));
            using var invitations = await client.GetAsync(
                new Uri("/api/admin/invitations", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, invitations.StatusCode);
            Assert.Contains(invitationId.ToString("D"), await invitations.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("new@example.test", await invitations.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(code!, await invitations.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var storedCode = await db.Invitations.AsNoTracking()
                    .Where(item => item.Id == invitationId)
                    .Select(item => item.Code).SingleAsync();
                Assert.Equal(InvitationCodeDigest.Compute(code!), storedCode);
                Assert.NotEqual(code, storedCode);
                db.Invitations.AddRange(Enumerable.Range(0, 51).Select(index => new Invitation(
                    Guid.NewGuid(), InvitationCodeDigest.Compute($"PAGED-{index}"),
                    $"paged-{index}@example.test", administratorId,
                    1, 0, clock.UtcNow.AddDays(30), true)));
                await db.SaveChangesAsync();
            }
            using var firstPage = await client.GetAsync(
                new Uri("/api/admin/invitations?page=0", UriKind.Relative));
            using (var pageBody = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync()))
            {
                Assert.Equal(50, pageBody.RootElement.GetProperty("items").GetArrayLength());
                Assert.True(pageBody.RootElement.GetProperty("hasMore").GetBoolean());
                Assert.DoesNotContain("PAGED-0", await firstPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            using var lastPage = await client.GetAsync(
                new Uri("/api/admin/invitations?page=1", UriKind.Relative));
            using (var pageBody = JsonDocument.Parse(await lastPage.Content.ReadAsStringAsync()))
            {
                Assert.Equal(2, pageBody.RootElement.GetProperty("items").GetArrayLength());
                Assert.False(pageBody.RootElement.GetProperty("hasMore").GetBoolean());
            }
            using var badPage = await client.GetAsync(
                new Uri("/api/admin/invitations?page=-1", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);

            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(90);
            using var revoked = await client.PostAsJsonAsync(
                $"/api/admin/invitations/{invitationId}/revoke", new { oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            using var invalidRegistration = await client.PostAsJsonAsync("/api/register",
                new { email = "new@example.test", displayName = "New User", locale = "en-US",
                    timeZone = "UTC", reportingCurrency = "USD", invitationCode = code,
                    password = "StrongUserPassword123!" });
            Assert.Equal(HttpStatusCode.BadRequest, invalidRegistration.StatusCode);

            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(120);
            using var replacement = await client.PostAsJsonAsync("/api/invitations",
                new { email = "new@example.test", oneTimeCode = "791148" });
            Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
            using var replacementJson = JsonDocument.Parse(await replacement.Content.ReadAsStringAsync());
            var newCode = replacementJson.RootElement.GetProperty("code").GetString();
            var replacementId = replacementJson.RootElement.GetProperty("id").GetGuid();
            using var wrongEmail = await client.PostAsJsonAsync("/api/register",
                new { email = "another@example.test", displayName = "Another", locale = "en-US",
                    timeZone = "UTC", reportingCurrency = "USD", invitationCode = newCode,
                    password = "StrongUserPassword123!" });
            Assert.Equal(HttpStatusCode.BadRequest, wrongEmail.StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.Equal(0, await db.Invitations.Where(item => item.Id == replacementId)
                    .Select(item => item.UsedCount).SingleAsync());
                await db.Invitations.Where(item => item.Id == replacementId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RecipientEmail, (string?)null));
            }
            using var legacyUnbound = await client.PostAsJsonAsync("/api/register",
                new { email = "new@example.test", displayName = "New User", locale = "en-US",
                    timeZone = "UTC", reportingCurrency = "USD", invitationCode = newCode,
                    password = "StrongUserPassword123!" });
            Assert.Equal(HttpStatusCode.BadRequest, legacyUnbound.StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.Equal(0, await db.Invitations.Where(item => item.Id == replacementId)
                    .Select(item => item.UsedCount).SingleAsync());
                await db.Invitations.Where(item => item.Id == replacementId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.RecipientEmail, "new@example.test"));
            }
            using var registered = await client.PostAsJsonAsync("/api/register",
                new { email = " NEW@EXAMPLE.TEST ", displayName = "New User", locale = "en-US",
                    timeZone = "UTC", reportingCurrency = "USD", invitationCode = newCode,
                    password = "StrongUserPassword123!" });
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
            using var replay = await client.PostAsJsonAsync("/api/register",
                new { email = "another@example.test", displayName = "Another", locale = "en-US",
                    timeZone = "UTC", reportingCurrency = "USD", invitationCode = newCode,
                    password = "StrongUserPassword123!" });
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

            using var verification = factory.Services.CreateScope();
            var saved = verification.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal(1, await saved.Users.CountAsync(user => user.Email == "NEW@EXAMPLE.TEST"));
            var audited = await saved.AuditEvents.Where(item => item.Action.StartsWith("Invitation.")
                    || item.Action == "User.Registered").ToListAsync();
            Assert.Equal(4, audited.Count);
            Assert.DoesNotContain(audited, item => item.After?.Contains(code!, StringComparison.Ordinal) == true
                || item.After?.Contains(newCode!, StringComparison.Ordinal) == true);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task PaperPlanManagementRequiresAValidatedAdminSessionAndFreshCodePerChange()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingPaperPlans_{Guid.NewGuid():N}",
            IntegratedSecurity = true, TrustServerCertificate = true, Encrypt = false
        }.ConnectionString;
        var secrets = new InMemorySecretStore();
        var clock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59));
        await using var factory = new AdminWebApplicationFactory(connection, secrets: secrets, clock: clock,
            enforcePaperWorkerLimits: true);
        var administratorId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(administratorId, "plan-admin@example.test", "Plan Admin",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    hasher.Hash("StrongAdminPassword123!")));
                db.Users.Add(new User(ownerId, "paper-owner@example.test", "Paper Owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongOwnerPassword123!")));
                db.Users.Add(new User(otherOwnerId, "other-paper-owner@example.test", "Other Owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongOwnerPassword123!")));
                await db.SaveChangesAsync();
            }
            await secrets.StoreSecretAsync($"admin-mfa-{administratorId:N}",
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA");
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var ownerClient = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var anonymous = await client.GetAsync(
                new Uri("/api/admin/entitlements/plans", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var ownerLogin = await ownerClient.PostAsJsonAsync("/api/login",
                new { email = "paper-owner@example.test", password = "StrongOwnerPassword123!" });
            Assert.Equal(HttpStatusCode.OK, ownerLogin.StatusCode);
            using var forbidden = await ownerClient.PostAsJsonAsync("/api/admin/entitlements/plans",
                new { code = "UNAUTHORIZED", name = "No access", maxExperimentWorkers = 10,
                    oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var login = await client.PostAsJsonAsync("/api/login",
                new { email = "plan-admin@example.test", password = "StrongAdminPassword123!",
                    oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(60);
            using var invalidCode = await client.PostAsJsonAsync("/api/admin/entitlements/plans",
                new { code = "PAPER-ONE", name = "One paper worker",
                    maxExperimentWorkers = 1, oneTimeCode = "000000" });
            Assert.Equal(HttpStatusCode.BadRequest, invalidCode.StatusCode);
            using var created = await client.PostAsJsonAsync("/api/admin/entitlements/plans",
                new { code = "PAPER-ONE", name = "One paper worker",
                    maxExperimentWorkers = 1, oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var planId = document.RootElement.GetProperty("id").GetGuid();
            Assert.False(document.RootElement.GetProperty("liveTradingEligible").GetBoolean());
            Assert.False(document.RootElement.GetProperty("futuresEligible").GetBoolean());
            using var replay = await client.PostAsJsonAsync("/api/admin/entitlements/plans",
                new { code = "PAPER-TWO", name = "Replay", maxExperimentWorkers = 2,
                    oneTimeCode = "882438" });
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            using var list = await client.GetAsync(
                new Uri("/api/admin/entitlements/plans", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            Assert.Contains("PAPER-ONE", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var livePlan = new Plan(Guid.NewGuid(), "LIVE-ELIGIBLE", "Reserved for a future live gate",
                1, liveTradingEligible: true);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                db.Plans.Add(livePlan);
                await db.SaveChangesAsync();
            }
            using var listAfterLivePlan = await client.GetAsync(
                new Uri("/api/admin/entitlements/plans", UriKind.Relative));
            Assert.DoesNotContain("LIVE-ELIGIBLE", await listAfterLivePlan.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            using var lookup = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners?email=paper-owner%40example.test", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
            Assert.Contains(ownerId.ToString("D"), await lookup.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
            using var forbiddenRead = await ownerClient.GetAsync(
                new Uri("/api/admin/entitlements/owners?email=paper-owner%40example.test", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenRead.StatusCode);
            using var forbiddenSearch = await ownerClient.GetAsync(
                new Uri("/api/admin/entitlements/owners/search?query=pap", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenSearch.StatusCode);
            using var search = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners/search?query=paper-owner&page=0", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            using (var found = JsonDocument.Parse(await search.Content.ReadAsStringAsync()))
            {
                Assert.Single(found.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal("paper-owner@example.test",
                    found.RootElement.GetProperty("items")[0].GetProperty("email").GetString());
                Assert.False(found.RootElement.GetProperty("hasMore").GetBoolean());
            }
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                db.Users.AddRange(Enumerable.Range(0, 51).Select(index => new User(
                    Guid.NewGuid(), $"paper-search-{index:D2}@example.test", "Search Owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active)));
                await db.SaveChangesAsync();
            }
            using var ownersPage0 = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners/search?query=paper-search&page=0", UriKind.Relative));
            using (var found = JsonDocument.Parse(await ownersPage0.Content.ReadAsStringAsync()))
            {
                Assert.Equal(50, found.RootElement.GetProperty("items").GetArrayLength());
                Assert.True(found.RootElement.GetProperty("hasMore").GetBoolean());
            }
            using var ownersPage1 = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners/search?query=paper-search&page=1", UriKind.Relative));
            using (var found = JsonDocument.Parse(await ownersPage1.Content.ReadAsStringAsync()))
            {
                Assert.Single(found.RootElement.GetProperty("items").EnumerateArray());
                Assert.False(found.RootElement.GetProperty("hasMore").GetBoolean());
            }
            using var noWildcard = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners/search?query=%25ap", UriKind.Relative));
            using (var found = JsonDocument.Parse(await noWildcard.Content.ReadAsStringAsync()))
                Assert.Empty(found.RootElement.GetProperty("items").EnumerateArray());
            using var invalidSearch = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners/search?query=pa&page=-1", UriKind.Relative));
            Assert.Equal(HttpStatusCode.BadRequest, invalidSearch.StatusCode);

            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(90);
            using var cannotAssignLive = await client.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/assign",
                new { planId = livePlan.Id, trialExpiresAtUtc = (DateTimeOffset?)null,
                    oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.BadRequest, cannotAssignLive.StatusCode);
            using var assigned = await client.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/assign",
                new { planId, trialExpiresAtUtc = clock.UtcNow.AddDays(1),
                    oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.Created, assigned.StatusCode);
            using var assignedJson = JsonDocument.Parse(await assigned.Content.ReadAsStringAsync());
            var entitlementId = assignedJson.RootElement.GetProperty("id").GetGuid();
            using var duplicate = await client.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/assign",
                new { planId, trialExpiresAtUtc = (DateTimeOffset?)null,
                    oneTimeCode = "975832" });
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using var current = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners?email=paper-owner%40example.test", UriKind.Relative));
            Assert.Contains(entitlementId.ToString("D"), await current.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
            using var ownerPlan = await ownerClient.GetAsync(
                new Uri("/api/entitlements/me", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, ownerPlan.StatusCode);
            var ownerPlanJson = await ownerPlan.Content.ReadAsStringAsync();
            Assert.Contains("PAPER-ONE", ownerPlanJson, StringComparison.Ordinal);
            Assert.DoesNotContain("LIVE-ELIGIBLE", ownerPlanJson, StringComparison.Ordinal);
            using (var body = JsonDocument.Parse(ownerPlanJson))
            {
                Assert.True(body.RootElement.GetProperty("paperWorkerLimitsEnabled").GetBoolean());
                Assert.Equal(1, body.RootElement.GetProperty("effectivePaperWorkerCapacity").GetInt32());
            }
            using var otherClient = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var otherLogin = await otherClient.PostAsJsonAsync("/api/login",
                new { email = "other-paper-owner@example.test", password = "StrongOwnerPassword123!" });
            Assert.Equal(HttpStatusCode.OK, otherLogin.StatusCode);
            using var otherPlan = await otherClient.GetAsync(
                new Uri("/api/entitlements/me", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, otherPlan.StatusCode);
            Assert.DoesNotContain("PAPER-ONE", await otherPlan.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            using (var body = JsonDocument.Parse(await otherPlan.Content.ReadAsStringAsync()))
                Assert.Equal(0, body.RootElement.GetProperty("effectivePaperWorkerCapacity").GetInt32());
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.Equal(1, await db.Users.Where(user => user.Id == ownerId)
                    .ExecuteUpdateAsync(update => update.SetProperty(user => user.Status, UserStatus.Suspended)));
                Assert.Null(await new EfEntitlementRepository(db, clock)
                    .GetEffectiveAsync(ownerId, clock.UtcNow));
                Assert.Equal(1, await db.Users.Where(user => user.Id == ownerId)
                    .ExecuteUpdateAsync(update => update.SetProperty(user => user.Status, UserStatus.Active)));
            }

            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(120);
            using var wrongOwner = await client.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{otherOwnerId}/revoke/{entitlementId}",
                new { oneTimeCode = "791148" });
            Assert.Equal(HttpStatusCode.Conflict, wrongOwner.StatusCode);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(150);
            using var extended = await client.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/trials/{entitlementId}/extend",
                new { trialExpiresAtUtc = clock.UtcNow.AddDays(2),
                    oneTimeCode = "744399" });
            Assert.Equal(HttpStatusCode.NoContent, extended.StatusCode);
            using var changedPlan = await ownerClient.GetAsync(new Uri("/api/entitlements/me", UriKind.Relative));
            Assert.Contains(clock.UtcNow.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                await changedPlan.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(180);
            using var revoked = await client.PostAsJsonAsync(
                $"/api/admin/entitlements/owners/{ownerId}/revoke/{entitlementId}",
                new { oneTimeCode = "668833" });
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            using var after = await client.GetAsync(
                new Uri("/api/admin/entitlements/owners?email=paper-owner%40example.test", UriKind.Relative));
            Assert.DoesNotContain(entitlementId.ToString("D"), await after.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);

            using var verification = factory.Services.CreateScope();
            var saved = verification.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Equal(2, await saved.Plans.CountAsync());
            Assert.Equal(6, await saved.AdministratorMfaRedemptions.CountAsync());
            var audits = await saved.AuditEvents.Where(item => item.Action.StartsWith("Entitlements."))
                .ToListAsync();
            Assert.Equal(4, audits.Count);
            Assert.All(audits, item => Assert.Equal(administratorId, item.ActorUserId));
            Assert.DoesNotContain(audits, item => item.After?.Contains("668833", StringComparison.Ordinal) == true);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ProductionAdministratorRequiresFreshSingleUseKeyVaultCode()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingAdminMfa_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        var secrets = new InMemorySecretStore();
        var clock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59));
        await using var factory = new AdminWebApplicationFactory(connection, secrets: secrets, clock: clock);
        var administratorId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                db.Users.Add(new User(administratorId, "verified-admin@example.test", "Verified Admin",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>()
                        .Hash("StrongAdminPassword123!")));
                await db.SaveChangesAsync();
            }
            await secrets.StoreSecretAsync($"admin-mfa-{administratorId:N}",
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA");

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var invalid = await client.PostAsJsonAsync("/api/login",
                new { email = "verified-admin@example.test", password = "StrongAdminPassword123!", oneTimeCode = "000000" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.False(invalid.Headers.Contains("Set-Cookie"));
            await secrets.RemoveSecretAsync($"admin-mfa-{administratorId:N}");
            using var unavailable = await client.PostAsJsonAsync("/api/login",
                new { email = "verified-admin@example.test", password = "StrongAdminPassword123!", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.False(unavailable.Headers.Contains("Set-Cookie"));
            await secrets.StoreSecretAsync($"admin-mfa-{administratorId:N}",
                "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA");
            using var valid = await client.PostAsJsonAsync("/api/login",
                new { email = "verified-admin@example.test", password = "StrongAdminPassword123!", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            using var me = await client.GetAsync(new Uri("/api/me", UriKind.Relative));
            Assert.Contains("\"isAdministrator\":true", await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var authorized = await client.GetAsync(new Uri("/api/universe/instruments", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
            using var auditResponse = await client.GetAsync(new Uri("/api/audit", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
            using var recovery = await client.PostAsJsonAsync(
                $"/api/paper-training/{administratorId}/workers/{Guid.NewGuid()}/reconcile-filled",
                new { correlationId = "paper-not-found", experimentHostStopped = true });
            Assert.Equal(HttpStatusCode.NotFound, recovery.StatusCode);

            using var anotherClient = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var replay = await anotherClient.PostAsJsonAsync("/api/login",
                new { email = "verified-admin@example.test", password = "StrongAdminPassword123!", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
            Assert.False(replay.Headers.Contains("Set-Cookie"));

            clock.UtcNow = DateTimeOffset.FromUnixTimeSeconds(89);
            using var adjacentReplay = await anotherClient.PostAsJsonAsync("/api/login",
                new { email = "verified-admin@example.test", password = "StrongAdminPassword123!", oneTimeCode = "119246" });
            Assert.Equal(HttpStatusCode.BadRequest, adjacentReplay.StatusCode);

            using var auditScope = factory.Services.CreateScope();
            var stored = auditScope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.Single(await stored.AdministratorMfaRedemptions.ToListAsync());
            var audit = Assert.Single(await stored.AuditEvents.Where(
                item => item.Action == "AdministratorMfaVerified").ToListAsync());
            Assert.DoesNotContain("119246", audit.After ?? "", StringComparison.Ordinal);

            for (var attempt = 5; attempt < 10; attempt++)
            {
                using var refused = await anotherClient.PostAsJsonAsync("/api/login",
                    new { email = "verified-admin@example.test", password = "IncorrectPassword123!" });
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }
            using var limited = await anotherClient.PostAsJsonAsync("/api/login",
                new { email = "verified-admin@example.test", password = "IncorrectPassword123!" });
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.Equal(1, await stored.Users.Where(user => user.Id == administratorId)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    user => user.MultiFactorAuthenticationEnabled, false)));
            using var revoked = await client.GetAsync(new Uri("/api/universe/instruments", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ProductionRevokesSessionsWhenAccountIsSuspendedOrRoleChanges()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingSessionRevocation_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        await using var factory = new AdminWebApplicationFactory(connection);
        var suspendedId = Guid.NewGuid();
        var changedRoleId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(suspendedId, "suspended@example.test", "Suspended Trader",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongPassword123!")));
                db.Users.Add(new User(changedRoleId, "role@example.test", "Role Changed Trader",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongPassword123!")));
                await db.SaveChangesAsync();
            }

            using var suspendedClient = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            using var changedRoleClient = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            using var suspendedLogin = await suspendedClient.PostAsJsonAsync("/api/login",
                new { email = "suspended@example.test", password = "StrongPassword123!" });
            using var changedRoleLogin = await changedRoleClient.PostAsJsonAsync("/api/login",
                new { email = "role@example.test", password = "StrongPassword123!" });
            Assert.Equal(HttpStatusCode.OK, suspendedLogin.StatusCode);
            Assert.Equal(HttpStatusCode.OK, changedRoleLogin.StatusCode);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.Equal(1, await db.Users.Where(user => user.Id == suspendedId)
                    .ExecuteUpdateAsync(update => update.SetProperty(user => user.Status, UserStatus.Suspended)));
                Assert.Equal(1, await db.Users.Where(user => user.Id == changedRoleId)
                    .ExecuteUpdateAsync(update => update.SetProperty(user => user.Role, RoleType.RiskOfficer)));
            }

            using var suspendedRequest = await suspendedClient.GetAsync(
                new Uri("/api/universe/instruments", UriKind.Relative));
            using var changedRoleRequest = await changedRoleClient.GetAsync(
                new Uri("/api/universe/instruments", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, suspendedRequest.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, changedRoleRequest.StatusCode);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ProductionRefusesPasswordOnlyAdministratorAndPreviouslyIssuedAdministratorCookies()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingAdminGate_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        await using var factory = new AdminWebApplicationFactory(connection);
        try
        {
            var administratorId = Guid.NewGuid();
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                Assert.Equal(6, (await db.Database.GetAppliedMigrationsAsync()).Count());
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(administratorId, "admin@example.test", "Administrator",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    hasher.Hash("StrongAdminPassword123!")));
                db.Users.Add(new User(Guid.NewGuid(), "trader@example.test", "Trader",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongTraderPassword123!")));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            using var denied = await client.PostAsJsonAsync("/api/login",
                new { email = "admin@example.test", password = "StrongAdminPassword123!" });
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.Contains("Invalid login", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.False(denied.Headers.Contains("Set-Cookie"));

            var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var identity = new ClaimsIdentity(
                [
                    new Claim("trading:user-id", administratorId.ToString()),
                    new Claim(ClaimTypes.Role, nameof(RoleType.Administrator))
                ], CookieAuthenticationDefaults.AuthenticationScheme);
            var cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IssuedUtc = DateTimeOffset.UtcNow,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
                },
                CookieAuthenticationDefaults.AuthenticationScheme));
            using var oldSession = new HttpRequestMessage(HttpMethod.Get, "/api/universe/instruments");
            oldSession.Headers.Add("Cookie", $"{options.Cookie.Name}={cookie}");
            using var refusedSession = await client.SendAsync(oldSession);
            Assert.Equal(HttpStatusCode.Unauthorized, refusedSession.StatusCode);

            using var signedIn = await client.PostAsJsonAsync("/api/login",
                new { email = "trader@example.test", password = "StrongTraderPassword123!" });
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            using var me = await client.GetAsync(new Uri("/api/me", UriKind.Relative));
            Assert.Contains("\"signedIn\":true", await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task DevelopmentDemoAdministratorCanStillSignInLocally()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingLocalAdmin_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        await using var factory = new AdminWebApplicationFactory(connection, "Development");
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.True(await db.Database.EnsureCreatedAsync());
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(Guid.Parse("8dd8e906-4ae0-4190-9082-64ec755c2913"),
                    "local-admin@example.test", "Local Demo Administrator",
                    "en-US", "UTC", "USD", RoleType.Administrator, true, UserStatus.Active,
                    hasher.Hash("LocalAdminPassword123!")));
                db.Users.Add(new User(Guid.NewGuid(), "local-other-admin@example.test",
                    "Local Non-demo Administrator", "en-US", "UTC", "USD",
                    RoleType.Administrator, true, UserStatus.Active,
                    hasher.Hash("LocalAdminPassword123!")));
                await db.SaveChangesAsync();
            }
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            using var other = await client.PostAsJsonAsync("/api/login",
                new { email = "local-other-admin@example.test",
                    password = "LocalAdminPassword123!" });
            Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
            using var signedIn = await client.PostAsJsonAsync("/api/login",
                new { email = "local-admin@example.test", password = "LocalAdminPassword123!" });
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            using var me = await client.GetAsync(new Uri("/api/me", UriKind.Relative));
            Assert.Contains("\"isAdministrator\":true",
                await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class AdminWebApplicationFactory(
        string connection, string environment = "Production", ISecretStore? secrets = null,
        TimeProvider? clock = null, bool enforcePaperWorkerLimits = false)
        : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("KeyVault:Uri", "https://auth-test.vault.azure.net/");
            builder.UseSetting("ConnectionStrings:TradingDb", connection);
            builder.UseSetting("Entitlements:PaperWorkerLimitsEnabled", enforcePaperWorkerLimits.ToString());
            builder.UseSetting("Development:SeedDemoData", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<TradingDbContext>>();
                services.RemoveAll<TradingDbContext>();
                services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connection));
                if (secrets is not null)
                {
                    services.RemoveAll<ISecretStore>();
                    services.AddSingleton(secrets);
                }
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
            });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
