extern alias WebApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using Trading.Application.Scanner;
using Trading.Domain.Market;
using Trading.Domain.Scanner;

namespace Trading.ArchitectureTests;

public sealed class ScannerResultsQueryTests
{
    [Fact]
    public async Task QueryIsOwnerScopedBoundedAndPreservesRepositoryRanking()
    {
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var request = Request(owner, requestId);
        var mine = Result(owner, requestId, runId, "BTC/USD", rank: 1, score: 1m);
        var second = Result(owner, requestId, runId, "ETH/USD", rank: 2, score: 1m);
        var other = Result(otherOwner, requestId, runId, "INJECTED", rank: 1, score: 1m);
        var requests = new RecordingRequests(request);
        var results = new RecordingResults([mine, second, other]);
        var service = new ScannerResultsQueryService(requests, results);

        var page = await service.GetAsync(owner, requestId, runId);

        Assert.NotNull(page);
        Assert.Equal(owner, requests.OwnerId);
        Assert.Equal(owner, results.OwnerId);
        Assert.Equal(ScannerResultsQueryService.PageSize, results.Limit);
        Assert.Equal(["BTC/USD", "ETH/USD"], page.Results.Select(row => row.Symbol));
        Assert.Equal([1, 2], page.Results.Select(row => row.Rank));
        Assert.Equal("Passed", Assert.Single(page.Results[0].Criteria).Status);
        Assert.Null(page.Results[0].DataRejectionReason);
    }

    [Fact]
    public async Task MissingOwnedRequestReturnsNoPageRatherThanAnotherOwnersEvidence()
    {
        var owner = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var requests = new RecordingRequests(null);
        var service = new ScannerResultsQueryService(requests, new RecordingResults([]));

        var page = await service.GetAsync(owner, requestId, Guid.NewGuid());

        Assert.Null(page);
    }

    [Fact]
    public async Task ExistingScanRunWithNoEligibleResultsReturnsAnEmptyPage()
    {
        var owner = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var service = new ScannerResultsQueryService(
            new RecordingRequests(Request(owner, requestId)),
            new RecordingResults([]));

        var page = await service.GetAsync(owner, requestId, Guid.NewGuid());

        Assert.NotNull(page);
        Assert.Empty(page.Results);
    }

    [Fact]
    public async Task ScannerRoutesRequireAuthentication()
    {
        await using var factory = new ScannerWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var api = await client.GetAsync(new Uri("/api/scanner/results", UriKind.Relative));
        var page = await client.GetAsync(new Uri("/scanner", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
    }

    [Fact]
    public void ScannerPageUsesTextContentAndHasNoActionsOrCredentialFields()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "wwwroot", "scanner-results.js"));
        var pageSource = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));

        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("submitTrade", script, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MapPost(\"/api/scanner", pageSource, StringComparison.Ordinal);
        Assert.Contains("does not submit trades", pageSource, StringComparison.Ordinal);
    }

    private static ScanRequest Request(Guid owner, Guid id) =>
        new(
            id,
            owner,
            "<unsafe scanner name>",
            ["BTC/USD"],
            CandleInterval.OneHour,
            [new ScanCriterion(ScanCriterionKind.MinimumClosedCandleCount, 10m)],
            10,
            DateTimeOffset.UnixEpoch);

    private static ScanResult Result(Guid owner, Guid requestId, Guid runId, string symbol, int rank, decimal score) =>
        new(
            owner,
            requestId,
            runId,
            symbol,
            rank,
            score,
            [ScanCriterionKind.MinimumClosedCandleCount],
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trading.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingRequests(ScanRequest? request) : IScanRequestRepository
    {
        internal Guid OwnerId { get; private set; }

        public Task AddAsync(Guid ownerId, ScanRequest scanRequest, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ScanRequest?> GetAsync(Guid ownerId, Guid scanRequestId, CancellationToken cancellationToken = default)
        {
            OwnerId = ownerId;
            return Task.FromResult(request?.OwnerId == ownerId && request.Id == scanRequestId ? request : null);
        }

        public Task<IReadOnlyList<ScanRequest>> ListAsync(Guid ownerId, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingResults(IReadOnlyList<ScanResult> all) : IScanResultRepository
    {
        internal Guid OwnerId { get; private set; }
        internal int Limit { get; private set; }

        public Task<ScanResultWriteResult> RecordAsync(Guid ownerId, ScanResult result, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScanResult>> ListAsync(
            Guid ownerId,
            Guid scanRequestId,
            Guid scanRunId,
            int limit,
            CancellationToken cancellationToken = default)
        {
            OwnerId = ownerId;
            Limit = limit;
            return Task.FromResult<IReadOnlyList<ScanResult>>(all
                .Where(result => result.OwnerId == ownerId &&
                                 result.ScanRequestId == scanRequestId &&
                                 result.ScanRunId == scanRunId)
                .ToArray());
        }
    }

    private sealed class ScannerWebApplicationFactory : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:SeedDemoData"] = "false",
                    ["ConnectionStrings:TradingDb"] = "Server=(localdb)\\MSSQLLocalDB;Database=ScannerResultsQueryTests;Trusted_Connection=True;"
                }));
        }
    }
}
