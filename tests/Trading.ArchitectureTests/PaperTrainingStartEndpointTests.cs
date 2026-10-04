extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trading.Application.Experiments;
using Trading.Application.Reporting;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Identity;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingStartEndpointTests
{
    [Fact]
    public async Task PaperTransactionReportIsOwnerScopedAuditedAndBlockedByUnresolvedClaims()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingPaperReport_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        var reportStore = new InMemoryReportStore();
        await using var factory = new PaperWebApplicationFactory(connection, reportStore);
        var owner = Guid.Parse("b1c5c9cb-1003-4979-96b3-3c3ddb78de75");
        var other = Guid.NewGuid();
        var from = DateTimeOffset.UtcNow.AddDays(-2);
        var to = from.AddDays(1);
        var worker = Guid.NewGuid();
        var pendingWorker = Guid.NewGuid();
        var pendingKey = new ExperimentDecisionKey(owner, pendingWorker, 1, ExperimentResearchGroup.A,
            "platform.ema-trend-continuation", 1, new string('A', 64), "ETH/USD",
            CandleInterval.OneMinute, from.AddMinutes(-2), from.AddMinutes(-1), from.AddMinutes(1));
        var pending = new ExperimentPaperExecutionAssociation(pendingKey,
            $"paper-report-{Guid.NewGuid():N}", ExperimentPaperExecutionStatus.Claimed);
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.True(await db.Database.EnsureCreatedAsync());
                db.Users.Add(new User(owner, "paper-report-owner@example.test", "Owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
                db.Users.Add(new User(other, "paper-report-other@example.test", "Other",
                    "en-US", "UTC", "EUR", RoleType.User, false, UserStatus.Active));
                await db.SaveChangesAsync();

                var own = new ExperimentWorker(worker, owner, "Owner report", "platform.ema-trend-continuation",
                    "BTC/USD", 1_000m, from.AddDays(-2), 1);
                own.Start();
                own.ApplyPaperTrade(1m, 100m, 0.1m, "buy", from.AddMinutes(-5), Guid.NewGuid());
                own.ApplyPaperTrade(1m, 123m, 0.2m, "sell", from.AddMinutes(5), Guid.NewGuid());
                var foreign = new ExperimentWorker(Guid.NewGuid(), other, "Other report",
                    "platform.ema-trend-continuation", "SOL/EUR", 1_000m, from.AddDays(-2), 2);
                foreign.Start();
                foreign.ApplyPaperTrade(1m, 10m, 0m, "buy", from.AddMinutes(-5), Guid.NewGuid());
                foreign.ApplyPaperTrade(1m, 12m, 0m, "sell", from.AddMinutes(5), Guid.NewGuid());
                var unresolved = new ExperimentWorker(pendingWorker, owner, "Pending report",
                    "platform.ema-trend-continuation", "ETH/USD", 1_000m, from.AddDays(-2), 3);
                unresolved.Start();
                var workers = new EfExperimentWorkerRepository(db);
                foreach (var item in new[] { own, foreign, unresolved })
                {
                    await workers.SaveAsync(item);
                    foreach (var fill in item.Ledger)
                        await workers.AddAsync(item.UserId, fill);
                }
                Assert.Equal(ExperimentPaperExecutionClaimResult.Claimed,
                    (await new EfExperimentPaperExecutionLedger(db).ClaimAsync(owner, pending)).Result);
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            var interval = new { fromUtc = from, toUtcExclusive = to };
            using var badTime = await client.PostAsJsonAsync("/api/reports/paper-transactions",
                new { fromUtc = from.ToOffset(TimeSpan.FromHours(1)), toUtcExclusive = to });
            Assert.Equal(HttpStatusCode.BadRequest, badTime.StatusCode);
            using var unsupportedCountry = await client.PostAsJsonAsync("/api/reports/paper-transactions",
                new { fromUtc = from, toUtcExclusive = to, countryProfileCode = "XX" });
            Assert.Equal(HttpStatusCode.BadRequest, unsupportedCountry.StatusCode);
            using var blocked = await client.PostAsJsonAsync("/api/reports/paper-transactions", interval);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            using var emptyBlocked = await client.PostAsJsonAsync("/api/reports/paper-transactions",
                new { fromUtc = from.AddHours(1), toUtcExclusive = from.AddHours(2) });
            Assert.Equal(HttpStatusCode.Conflict, emptyBlocked.StatusCode);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.Equal(0, await db.AuditEvents.CountAsync(
                    audit => audit.Action == "Reporting.PaperTransactionsGenerated"));
                await new EfExperimentPaperExecutionLedger(db).CompleteAsync(owner,
                    pending with { Status = ExperimentPaperExecutionStatus.Blocked });
            }

            using var response = await client.PostAsJsonAsync("/api/reports/paper-transactions", interval);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var body = json.RootElement;
            var row = Assert.Single(body.GetProperty("transactions").EnumerateArray());
            Assert.Equal(worker.ToString("D"), row.GetProperty("workerId").GetString());
            Assert.Equal("BTC/USD", row.GetProperty("symbol").GetString());
            Assert.Equal("22.7", row.GetProperty("realizedProfitAndLoss").GetString());
            Assert.Equal("122.8", row.GetProperty("cashChange").GetString());
            Assert.Equal("22.7", body.GetProperty("totalInReportingCurrency").GetString());
            Assert.Equal("USD", body.GetProperty("reportingCurrency").GetString());
            using var saved = await client.PostAsJsonAsync("/api/reports/paper-transactions",
                new { fromUtc = from, toUtcExclusive = to, export = true, countryProfileCode = "GB" });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            using var savedJson = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
            var exportId = savedJson.RootElement.GetProperty("exportId").GetGuid();
            Assert.NotEqual(Guid.Empty, exportId);
            var example = savedJson.RootElement.GetProperty("countryProfile");
            Assert.Equal("GB", example.GetProperty("code").GetString());
            Assert.Contains("not a UK tax return", example.GetProperty("disclaimer").GetString(), StringComparison.Ordinal);
            using var list = await client.GetAsync(
                new Uri("/api/reports/paper-transactions/exports", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var listed = Assert.Single(listJson.RootElement.EnumerateArray());
            Assert.Equal(exportId, listed.GetProperty("id").GetGuid());
            Assert.Equal("GB", listed.GetProperty("countryProfileCode").GetString());
            var downloadPath = $"/api/reports/paper-transactions/exports/{exportId:D}";
            using var download = await client.GetAsync(new Uri(downloadPath, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
            Assert.Contains("attachment", download.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
            using var downloadedJson = JsonDocument.Parse(await download.Content.ReadAsStringAsync());
            Assert.Equal("22.7", Assert.Single(downloadedJson.RootElement.GetProperty("transactions").EnumerateArray())
                .GetProperty("realizedProfitAndLoss").GetString());
            using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, downloadPath);
            foreignRequest.Headers.Add("X-Paper-Test-User-Id", other.ToString("D"));
            using var denied = await client.SendAsync(foreignRequest);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            using var foreignListRequest = new HttpRequestMessage(HttpMethod.Get,
                "/api/reports/paper-transactions/exports");
            foreignListRequest.Headers.Add("X-Paper-Test-User-Id", other.ToString("D"));
            using var foreignList = await client.SendAsync(foreignListRequest);
            Assert.Equal("[]", await foreignList.Content.ReadAsStringAsync());
            reportStore.Corrupt(owner, exportId);
            using var corrupted = await client.GetAsync(new Uri(downloadPath, UriKind.Relative));
            Assert.Equal(HttpStatusCode.Conflict, corrupted.StatusCode);
            using var scopeAfter = factory.Services.CreateScope();
            var persisted = scopeAfter.ServiceProvider.GetRequiredService<TradingDbContext>();
            var auditRecords = await persisted.AuditEvents.AsNoTracking()
                .Where(value => value.Action == "Reporting.PaperTransactionsGenerated").ToListAsync();
            Assert.Equal(2, auditRecords.Count);
            Assert.All(auditRecords, auditRecord => Assert.Equal(owner, auditRecord.ActorUserId));
            Assert.All(auditRecords, auditRecord =>
                Assert.DoesNotContain(other.ToString("D"), auditRecord.After!, StringComparison.Ordinal));
            var exportRecord = Assert.Single(await persisted.AuditEvents.AsNoTracking()
                .Where(value => value.Action == "Reporting.PaperTransactionsExported").ToListAsync());
            Assert.Equal(owner, exportRecord.ActorUserId);
            Assert.Single(await persisted.AuditEvents.AsNoTracking()
                .Where(value => value.Action == "Reporting.PaperTransactionsDownloaded").ToListAsync());
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ReportingPreferencesAreOwnedValidatedAndPersistedWithAudit()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingReportingProfile_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        await using var factory = new PaperWebApplicationFactory(connection);
        var owner = Guid.Parse("b1c5c9cb-1003-4979-96b3-3c3ddb78de75");
        var other = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.True(await db.Database.EnsureCreatedAsync());
                db.Users.Add(new User(owner, "reporting-owner@example.test", "Owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
                db.Users.Add(new User(other, "reporting-other@example.test", "Other",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
                await db.SaveChangesAsync();
            }
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
            });
            using var oldProfile = await client.GetAsync(new Uri("/api/reporting/profile", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, oldProfile.StatusCode);
            Assert.Contains("\"reportingCurrency\":\"USD\"",
                await oldProfile.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var changed = await client.PutAsJsonAsync("/api/reporting/profile",
                new { locale = "fr-FR", timeZone = "Asia/Tokyo", reportingCurrency = "EUR" });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            using var disabledExport = await client.PostAsJsonAsync("/api/reports/paper-transactions",
                new { fromUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    toUtcExclusive = DateTimeOffset.UtcNow.AddMinutes(-1), export = true });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledExport.StatusCode);
            using var invalid = await client.PutAsJsonAsync("/api/reporting/profile",
                new { locale = "fr-FR", timeZone = "Unknown/Location", reportingCurrency = "ZZZ" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            using var scopeAfter = factory.Services.CreateScope();
            var persisted = scopeAfter.ServiceProvider.GetRequiredService<TradingDbContext>();
            var ownerRecord = await persisted.Users.AsNoTracking().SingleAsync(user => user.Id == owner);
            Assert.Equal("fr-FR", ownerRecord.Locale);
            Assert.Equal("Asia/Tokyo", ownerRecord.TimeZone);
            Assert.Equal("EUR", ownerRecord.ReportingCurrency);
            var otherRecord = await persisted.Users.AsNoTracking().SingleAsync(user => user.Id == other);
            Assert.Equal("USD", otherRecord.ReportingCurrency);
            var audit = Assert.Single(await persisted.AuditEvents.AsNoTracking()
                .Where(value => value.Action == "Reporting.ProfileChanged").ToListAsync());
            Assert.Equal(owner, audit.ActorUserId);
            Assert.Contains("Asia/Tokyo", audit.After, StringComparison.Ordinal);
            Assert.DoesNotContain("Unknown/Location", audit.After, StringComparison.Ordinal);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task PaperStartNeedsBothHostsButNoPrivateExchangeAccount()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;
        var database = $"TradingPaperStart_{Guid.NewGuid():N}";
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        await using var factory = new PaperWebApplicationFactory(connection);
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                Assert.Equal(database, db.Database.GetDbConnection().Database);
                Assert.True(await db.Database.EnsureCreatedAsync());
                db.Users.Add(new User(
                    Guid.Parse("b1c5c9cb-1003-4979-96b3-3c3ddb78de75"),
                    "paper-start-owner@example.test", "Paper start owner",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active));
                await db.SaveChangesAsync();
                Assert.Empty(await db.ExchangeAccounts.ToListAsync());
            }
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            using var initial = await client.PostAsJsonAsync("/api/paper-training", new { });
            Assert.Equal(HttpStatusCode.Conflict, initial.StatusCode);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var heartbeats = new EfPaperHostHeartbeatRepository(db);
                await heartbeats.RecordAsync(EfPaperHostHeartbeatRepository.Scanner, DateTimeOffset.UtcNow);
            }
            using var onlyScanner = await client.PostAsJsonAsync("/api/paper-training", new { });
            Assert.Equal(HttpStatusCode.Conflict, onlyScanner.StatusCode);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await new EfPaperHostHeartbeatRepository(db)
                    .RecordAsync(EfPaperHostHeartbeatRepository.Experiments, DateTimeOffset.UtcNow);
            }
            using var started = await client.PostAsJsonAsync("/api/paper-training", new { });
            Assert.Equal(HttpStatusCode.Conflict, started.StatusCode);
            Assert.Contains("forward Kraken stream", await started.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var notReady = await client.GetAsync(new Uri("/api/paper-training", UriKind.Relative));
            Assert.Contains("\"forwardFeedObservedRecently\":false",
                await notReady.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var now = DateTimeOffset.UtcNow;
                var close = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero);
                await new EfPaperHostHeartbeatRepository(db).RecordForwardCandleAsync(
                    new Candle("BTC/USD", CandleInterval.FiveMinutes,
                        close.AddMinutes(-5), close, 100m, 101m, 99m, 100m, 1m, true, false), now);
            }
            using var ready = await client.PostAsJsonAsync("/api/paper-training", new { });
            Assert.True(ready.StatusCode == HttpStatusCode.Accepted,
                $"Paper start returned {ready.StatusCode}: {await ready.Content.ReadAsStringAsync()}");
            using var state = await client.GetAsync(new Uri("/api/paper-training", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            Assert.Contains("\"state\":\"Active\"", await state.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Contains("\"forwardFeedObservedRecently\":true",
                await state.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            using var workers = await client.GetAsync(new Uri("/api/experiments", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, workers.StatusCode);
            using var audit = await client.GetAsync(new Uri("/api/audit", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Forbidden, audit.StatusCode);
            using var recovery = await client.PostAsJsonAsync(
                $"/api/paper-training/{Guid.NewGuid()}/workers/{Guid.NewGuid()}/reconcile-filled",
                new { correlationId = "paper-probe", experimentHostStopped = true });
            Assert.Equal(HttpStatusCode.Forbidden, recovery.StatusCode);
            using var forgedAudit = await client.PostAsJsonAsync("/api/audit",
                new { action = "PaperExecution.FilledReconciled" });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, forgedAudit.StatusCode);
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class PaperWebApplicationFactory(
        string connection, IPaperTransactionReportStore? reportStore = null)
        : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("KeyVault:Uri", "https://paper-test.vault.azure.net/");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<TradingDbContext>>();
                services.RemoveAll<TradingDbContext>();
                services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connection));
                if (reportStore is not null)
                    services.AddSingleton(reportStore);
                services.AddSingleton<IOptions<PaperTrainingPrerequisiteOptions>>(
                    Options.Create(new PaperTrainingPrerequisiteOptions
                    {
                        DurableClosedCandleSource = true,
                        ApprovedResearchGroupsAndGates = true,
                        WorkerRiskPolicy = true,
                        PaperFillPolicy = true,
                        OutputLedger = true,
                        ProtectiveScheduler = true
                    }));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "PaperTest";
                    options.DefaultChallengeScheme = "PaperTest";
                }).AddScheme<AuthenticationSchemeOptions, PaperTestAuthHandler>("PaperTest", _ => { });
            });
        }
    }

    private sealed class InMemoryReportStore : IPaperTransactionReportStore
    {
        private readonly Dictionary<(Guid Owner, Guid Report), byte[]> _content = [];

        public Task StoreAsync(Guid ownerId, Guid reportId, ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_content.TryAdd((ownerId, reportId), content.ToArray()))
                throw new InvalidOperationException("A report id cannot be reused.");
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(Guid ownerId, Guid reportId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_content[(ownerId, reportId)]);
        }

        public void Corrupt(Guid ownerId, Guid reportId)
        {
            var content = _content[(ownerId, reportId)];
            content[0] ^= 1;
        }
    }
}

public sealed class PaperTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Context.Request.Headers.TryGetValue("X-Paper-Test-User-Id", out var header)
            && Guid.TryParse(header.ToString(), out var providedId)
            ? providedId
            : Guid.Parse("b1c5c9cb-1003-4979-96b3-3c3ddb78de75");
        var claims = new[]
        {
            new Claim("trading:user-id", userId.ToString()),
            new Claim(ClaimTypes.Role, "User")
        };
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
    }
}
