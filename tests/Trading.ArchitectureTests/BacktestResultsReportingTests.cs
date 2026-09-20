extern alias WebApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using Trading.Application.Backtesting;
using Trading.Backtesting;

namespace Trading.ArchitectureTests;

public sealed class BacktestResultsReportingTests
{
    [Fact]
    public async Task QueryIsOwnerScopedAndExcludesPlatformOwnedEvidence()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var source = new SuppliedBacktestResultSource(
        [
            Envelope(owner, 1234.5678m),
            Envelope(other, 999m),
            Envelope(null, 888m)
        ]);
        var service = new BacktestResultsQueryService(source);

        var page = await service.ListAsync(owner, 0);

        var report = Assert.Single(page.Results);
        Assert.Equal("1234.5678", report.InitialPortfolio);
        Assert.Equal("1300.5678", report.FinalPortfolio);
        Assert.Equal("66", report.NetPnl);
        Assert.Equal("2.25", report.TotalFees);
        Assert.Equal("0.5", report.TotalSlippage);
        Assert.Equal(2, report.EventCount);
        Assert.Equal(1, report.RejectedEventCount);
        Assert.Equal("Simulation rejected", report.Events[1].Status);
        Assert.Equal("Minimum notional was not met.", report.Events[1].Rationale);
        Assert.Equal("1.25", report.EquitySnapshots[0].Equity);
    }

    [Fact]
    public async Task UnavailableStoreReturnsTheExplicitEmptyReportPage()
    {
        var page = await new BacktestResultsQueryService(new UnavailableBacktestResultSource())
            .ListAsync(Guid.NewGuid(), 0);

        Assert.Empty(page.Results);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void EnvelopeRequiresReproducibilityIdentityAndUtcProvenance()
    {
        var resultWithoutDatasetIdentity = new BacktestResult(
            "template-id",
            "BTC/USD",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            1m,
            1m,
            0m,
            0m,
            0m,
            0,
            null,
            [],
            []);

        Assert.Throws<ArgumentException>(() => new BacktestResultEnvelope(
            Guid.NewGuid(),
            Guid.NewGuid(),
            resultWithoutDatasetIdentity,
            "template-v1",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            "verified archive"));
    }

    [Fact]
    public async Task BacktestRoutesRequireAuthentication()
    {
        await using var factory = new BacktestWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var api = await client.GetAsync(new Uri("/api/backtests/results", UriKind.Relative));
        var page = await client.GetAsync(new Uri("/backtests", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
    }

    [Fact]
    public void BacktestRendererUsesSafeTextAndHasNoExecutionControls()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "wwwroot", "backtest-results.js"));
        var pageSource = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));
        var pageStart = pageSource.IndexOf("app.MapGet(\"/backtests\"", StringComparison.Ordinal);
        var pageEnd = pageSource.IndexOf("app.MapGet(\"/scanner\"", pageStart, StringComparison.Ordinal);
        var backtestPage = pageSource[pageStart..pageEnd];

        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("method: 'POST'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", backtestPage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("button", backtestPage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No completed reproducible backtest is available yet.", script, StringComparison.Ordinal);
        Assert.Contains("Hypothetical historical results.", backtestPage, StringComparison.Ordinal);
        Assert.Contains("do not represent an order", backtestPage, StringComparison.Ordinal);
        Assert.Contains("live or paper execution", backtestPage, StringComparison.Ordinal);
    }

    private static BacktestResultEnvelope Envelope(Guid? owner, decimal initialPortfolio)
    {
        var start = DateTimeOffset.UnixEpoch;
        var result = new BacktestResult(
            "template-id",
            "<BTC/USD>",
            start,
            start.AddHours(2),
            initialPortfolio,
            initialPortfolio + 66m,
            66m,
            2.25m,
            0.5m,
            1,
            "dataset-version-2026-09-20",
            [
                new BacktestEvent(
                    start.AddHours(1),
                    null!,
                    BacktestSimulatedAction.Buy,
                    "<recorded event>",
                    1m,
                    100m,
                    100m,
                    1m,
                    100m,
                    0m,
                    0.25m),
                new BacktestEvent(
                    start.AddHours(2),
                    null!,
                    BacktestSimulatedAction.Rejected,
                    "Minimum notional was not met.",
                    0m,
                    0m,
                    100m,
                    1m)
            ],
            [new BacktestEquitySnapshot(start.AddHours(2), 1m, 0.25m, 1m, 1.25m)]);

        return new BacktestResultEnvelope(
            Guid.NewGuid(),
            owner,
            result,
            "template-v1",
            start.AddHours(3),
            start.AddHours(2),
            "verified historical archive");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trading.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class BacktestWebApplicationFactory : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:SeedDemoData"] = "false",
                    ["ConnectionStrings:TradingDb"] = "Server=(localdb)\\MSSQLLocalDB;Database=BacktestResultsReportingTests;Trusted_Connection=True;"
                }));
        }
    }
}
