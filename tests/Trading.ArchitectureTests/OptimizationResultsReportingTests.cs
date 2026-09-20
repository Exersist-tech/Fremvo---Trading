extern alias WebApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Globalization;
using Trading.Application.Optimization;
using Trading.Optimization;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class OptimizationResultsReportingTests
{
    [Fact]
    public async Task QueryIsOwnerScopedAndOrdersCandidatesAndFoldsDeterministically()
    {
        var owner = Guid.NewGuid();
        var report = Envelope(owner);
        var page = await new OptimizationResultsQueryService(
            new SuppliedOptimizationResearchReportSource([report, Envelope(Guid.NewGuid()), Envelope(null)]))
            .ListAsync(owner, 0);

        var result = Assert.Single(page.Results);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal([1, 2], result.Candidates.Select(candidate => candidate.Rank));
        Assert.Equal(["2", "1"], result.Candidates.Select(candidate => candidate.Parameters.Single().Value));
        Assert.Equal(["fold-earlier", "fold-later"], result.WalkForwardFolds.Select(fold => fold.Id));
        Assert.NotNull(result.WalkForwardAggregates);
        Assert.Equal("2", result.WalkForwardAggregates!.FoldCount.ToString(CultureInfo.InvariantCulture));
        Assert.NotNull(result.HoldoutVerification);
        Assert.Contains("not used to rank", result.HoldoutVerification!.Status, StringComparison.Ordinal);
        Assert.All(result.WalkForwardFolds, fold => Assert.Contains("UTC", fold.ValidationFromUtc, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableSourceReturnsTruthfulEmptyPage()
    {
        var page = await new OptimizationResultsQueryService(new UnavailableOptimizationResearchReportSource())
            .ListAsync(Guid.NewGuid(), 0);

        Assert.Empty(page.Results);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void PlanValidationCannotSkipTheRequiredHoldoutWindow()
    {
        var result = OptimizationPlanValidator.Validate(new OptimizationPlanRequest(
            "BTC/USD",
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
            default,
            default,
            [new StrategyParameterDefinition("lookback", 1m, 2m, 1m, "test")]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Each split end", StringComparison.Ordinal));
        Assert.False(result.CanBeExecuted);
    }

    [Fact]
    public async Task OptimizationRoutesRequireAuthentication()
    {
        await using var factory = new OptimizationWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("/api/optimization/results", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("/optimization", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(new Uri("/api/optimization/validate-plan", UriKind.Relative), null)).StatusCode);
    }

    [Fact]
    public void RendererEscapesStringsAndContainsNoExecutionControls()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "wwwroot", "optimization-results.js"));
        var pageSource = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));
        var pageStart = pageSource.IndexOf("app.MapGet(\"/optimization\"", StringComparison.Ordinal);
        var pageEnd = pageSource.IndexOf("app.MapGet(\"/optimization/plan-validation\"", pageStart, StringComparison.Ordinal);
        var page = pageSource[pageStart..pageEnd];

        Assert.Contains("textContent", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("method: 'POST'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hypothetical research only.", page, StringComparison.Ordinal);
        Assert.Contains("holdout is locked", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not financial advice", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not an actionable", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No completed optimization research report is available from a durable source.", script, StringComparison.Ordinal);
    }

    private static OptimizationResearchReportEnvelope Envelope(Guid? owner)
    {
        var dataset = DatasetSplitTestFactory.Create();
        var training = Split("training", DatasetSplitType.Training, dataset, 0, 10);
        var validation = Split("validation", DatasetSplitType.Validation, dataset, 10, 20);
        var holdout = Split("holdout", DatasetSplitType.Holdout, dataset, 20, 30);
        var alpha = new StrategyParameterDefinition("alpha", 1m, 2m, 1m, "test");
        var alphaParameters = new StrategyParameterSet([alpha], new Dictionary<string, StrategyParameterValue>
        {
            ["alpha"] = StrategyParameterValue.FromNumeric(1m)
        });
        var folds = new WalkForwardEvaluationResult(
        [
            new WalkForwardFold("fold-later", Split("fold-later-training", DatasetSplitType.Training, dataset, 0, 8),
                Split("fold-later-validation", DatasetSplitType.Validation, dataset, 8, 10), alphaParameters, 2m),
            new WalkForwardFold("fold-earlier", Split("fold-earlier-training", DatasetSplitType.Training, dataset, 0, 4),
                Split("fold-earlier-validation", DatasetSplitType.Validation, dataset, 4, 8), alphaParameters, 1m)
        ]);
        var run = new OptimizationRun("research<&>", training, validation, holdout, [alpha]);
        var result = run.Execute((parameters, _) => parameters.GetDecimal("alpha"), 2, folds);

        return new OptimizationResearchReportEnvelope(
            Guid.NewGuid(),
            owner,
            "research<&>",
            result,
            training,
            validation,
            holdout,
            new OptimizationHoldoutVerification(result.HoldoutScore, DateTimeOffset.UnixEpoch.AddDays(31)),
            DateTimeOffset.UnixEpoch.AddDays(32),
            "trusted <archive>");
    }

    private static DatasetSplit Split(string id, DatasetSplitType type, Trading.Backtesting.HistoricalDataset dataset, int fromDay, int toDay) =>
        new(id, type, dataset,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(fromDay),
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(toDay),
            10);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trading.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class OptimizationWebApplicationFactory : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:SeedDemoData"] = "false",
                    ["ConnectionStrings:TradingDb"] = "Server=(localdb)\\MSSQLLocalDB;Database=OptimizationResultsReportingTests;Trusted_Connection=True;"
                }));
        }
    }
}
