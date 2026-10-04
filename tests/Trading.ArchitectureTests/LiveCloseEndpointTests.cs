extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trading.Application.Execution;
using Trading.Application.UseCases.Identity;
using Trading.Domain.Identity;
using Trading.Domain.Orders;
using Trading.Domain.Users;
using Trading.Infrastructure.Data;

namespace Trading.ArchitectureTests;

public sealed class LiveCloseEndpointTests
{
    [Fact]
    public async Task CloseRouteUsesAuthenticatedOwnerAndNeverAcceptsAnAnonymousRequest()
    {
        var server = Environment.GetEnvironmentVariable("TRADING_SQL_INTEGRATION_SERVER");
        if (string.IsNullOrWhiteSpace(server))
            return;

        var connection = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = $"TradingLiveClose_{Guid.NewGuid():N}",
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            Encrypt = false
        }.ConnectionString;
        var service = new RecordingLiveService();
        await using var factory = new LiveCloseWebApplicationFactory(connection, service);
        var ownerId = Guid.NewGuid();
        var positionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        try
        {
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.MigrateAsync();
                var hasher = scope.ServiceProvider.GetRequiredService<Pbkdf2PasswordHasher>();
                db.Users.Add(new User(ownerId, "live-close@example.test", "Live close",
                    "en-US", "UTC", "USD", RoleType.User, false, UserStatus.Active,
                    hasher.Hash("StrongLiveClosePassword123!")));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
            var path = $"/api/live/positions/{positionId:D}/close";
            var request = new { exchangeAccountId = accountId, quantity = 0.001m, clientOrderId = "close-test" };
            using var anonymous = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Null(service.OwnerId);

            using var login = await client.PostAsJsonAsync("/api/login",
                new { email = "live-close@example.test", password = "StrongLiveClosePassword123!" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using var close = await client.PostAsJsonAsync(path, request);
            Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
            Assert.Equal(ownerId, service.OwnerId);
            Assert.Equal(accountId, service.AccountId);
            Assert.Equal(positionId, service.PositionId);
            Assert.Equal(0.001m, service.Quantity);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Database.EnsureDeletedAsync();
        }
    }

    private sealed class LiveCloseWebApplicationFactory(string connection, RecordingLiveService service)
        : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:SeedDemoData"] = "false",
                    ["ConnectionStrings:TradingDb"] = connection
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILiveTradingService>();
                services.AddSingleton<ILiveTradingService>(service);
            });
        }
    }

    private sealed class RecordingLiveService : ILiveTradingService
    {
        public Guid? OwnerId { get; private set; }
        public Guid? AccountId { get; private set; }
        public Guid? PositionId { get; private set; }
        public decimal? Quantity { get; private set; }

        public Task<LiveTradeResult> SubmitAsync(Guid userId, Guid exchangeAccountId,
            string symbol, OrderSide side, decimal quantity, string? clientOrderId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The close route must not use the entry path.");

        public Task<LiveTradeResult> ClosePositionAsync(Guid userId, Guid exchangeAccountId,
            Guid positionId, decimal quantity, string? clientOrderId,
            CancellationToken cancellationToken = default)
        {
            OwnerId = userId;
            AccountId = exchangeAccountId;
            PositionId = positionId;
            Quantity = quantity;
            return Task.FromResult(LiveTradeResult.Failure(
                LiveTradeOutcome.Blocked, "The fake execution service sends no orders."));
        }
    }
}
