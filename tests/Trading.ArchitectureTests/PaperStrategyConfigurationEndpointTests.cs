extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trading.Application.Experiments;
using Trading.Application.UseCases.Identity;
using Trading.Domain.Identity;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.ArchitectureTests;

public sealed class PaperStrategyConfigurationEndpointTests
{
    [Fact]
    public async Task ActiveOwnerCanEditFutureAssignmentWithoutChangingReservedWorker()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingStrategyEndpoint_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        await using var factory = new StrategyWebApplicationFactory(connection);
        var owner = Guid.NewGuid();
        var reserved = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "BTC/USD",
            ProvenanceId = "scan-1234567890ABCDEF12345678"
        };
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                db.Users.Add(new User(owner, "paper@example.test", "Paper Trader",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>()
                        .Hash("StrongPaperPassword123!")));
                await db.SaveChangesAsync();

                var activation = scope.ServiceProvider.GetRequiredService<PaperTrainingActivationService>();
                await activation.StartScannerAsync(owner, owner, RoleType.User,
                    new(true, true, true, true, true, true));
                var repository = new EfPaperTrainingActivationRepository(db);
                var current = (await repository.GetAsync(owner))!;
                Assert.True(await repository.TrySaveAsync(
                    current with { Slots = [reserved] }, PaperTrainingActivationState.Active));
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            using var signedIn = await client.PostAsJsonAsync("/api/login",
                new { email = "paper@example.test", password = "StrongPaperPassword123!" });
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

            var assignments = Enumerable.Range(1, 10)
                .Select(slot => new
                {
                    slot,
                    strategyId = slot == 1
                        ? PaperTrainingActivationService.ThreeSwingChannelDivergenceStrategyId
                        : PaperTrainingActivationService.ApprovedSlots[0].StrategyId,
                    strategyParametersJson = "{}"
                })
                .ToArray();
            using var saved = await client.PutAsJsonAsync("/api/paper-training/strategies",
                new { assignments });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            using var response = await client.GetAsync(new Uri("/api/paper-training", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Active", body.RootElement.GetProperty("state").GetString());
            var assignment = body.RootElement.GetProperty("workerAssignments")[0];
            Assert.Equal("Reserved", assignment.GetProperty("state").GetString());
            Assert.Equal(PaperTrainingActivationService.ThreeSwingChannelDivergenceStrategyId,
                assignment.GetProperty("strategyId").GetString());
            Assert.Equal(reserved.StrategyId, assignment.GetProperty("currentStrategyId").GetString());
            Assert.Equal(reserved.StrategyVersion, assignment.GetProperty("currentStrategyVersion").GetInt32());
        }
        finally
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlServer(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class StrategyWebApplicationFactory(string connection)
        : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("KeyVault:Uri", "https://auth-test.vault.azure.net/");
            builder.UseSetting("ConnectionStrings:TradingDb", connection);
            builder.UseSetting("Development:SeedDemoData", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<TradingDbContext>>();
                services.RemoveAll<TradingDbContext>();
                services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connection));
            });
        }
    }
}
