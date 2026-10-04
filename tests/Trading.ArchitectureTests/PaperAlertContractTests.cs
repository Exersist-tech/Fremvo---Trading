namespace Trading.ArchitectureTests;

public sealed class PaperAlertContractTests
{
    [Fact]
    public void LiveOrderAnomalyAlertsHaveAnUnsampledWebTelemetrySource()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var bicep = File.ReadAllText(Path.Combine(root, "deploy", "bicep", "main.bicep"));
        var web = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));
        var live = File.ReadAllText(Path.Combine(root, "src", "Trading.Application",
            "Execution", "LiveTradingService.cs"));

        Assert.Contains("resource unknownLiveOrdersAlert", bicep, StringComparison.Ordinal);
        Assert.Contains("resource rejectedLiveOrdersAlert", bicep, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddApplicationInsightsTelemetry", web, StringComparison.Ordinal);
        Assert.Contains("options.EnableAdaptiveSampling = false", web, StringComparison.Ordinal);
        Assert.Contains("module.EnableSqlCommandTextInstrumentation = false", web, StringComparison.Ordinal);
        Assert.Contains("typeof(LiveTradingService).FullName!, LogLevel.Warning", web, StringComparison.Ordinal);
        Assert.Contains("LiveOrderUnknown", live, StringComparison.Ordinal);
        Assert.Contains("LiveOrderRejected", live, StringComparison.Ordinal);
    }

    [Fact]
    public void ExperimentAlertsMatchExistingSafeTelemetryAndRemainOptIn()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var bicep = File.ReadAllText(Path.Combine(root, "deploy", "bicep", "main.bicep"));
        var worker = File.ReadAllText(Path.Combine(root, "src",
            "Trading.Workers.Experiments", "Worker.cs"));
        var host = File.ReadAllText(Path.Combine(root, "src",
            "Trading.Workers.Experiments", "Program.cs"));
        var marketHost = File.ReadAllText(Path.Combine(root, "src",
            "Trading.Workers.MarketData", "Program.cs"));
        var scanner = File.ReadAllText(Path.Combine(root, "src",
            "Trading.Workers.MarketData", "ContinuousPaperScannerWorker.cs"));
        Assert.Contains("resource paperExperimentTickMissingAlert", bicep, StringComparison.Ordinal);
        Assert.Contains("resource paperExperimentFaultAlert", bicep, StringComparison.Ordinal);
        Assert.Contains("enabled: paperWorkerHostsEnabled", bicep, StringComparison.Ordinal);
        Assert.Contains("Experiment tick completed.", bicep, StringComparison.Ordinal);
        Assert.Contains("Workers faulted: 0.", bicep, StringComparison.Ordinal);
        Assert.Contains("An experiment pool faulted", bicep, StringComparison.Ordinal);
        Assert.Contains("Experiment tick completed.", worker, StringComparison.Ordinal);
        Assert.Contains("Workers faulted: {Faulted}.", worker, StringComparison.Ordinal);
        Assert.Contains("An experiment pool faulted", worker, StringComparison.Ordinal);
        Assert.Contains("typeof(Worker).FullName!, LogLevel.Information", host, StringComparison.Ordinal);
        Assert.Contains("typeof(ContinuousPaperScannerWorker).FullName!, LogLevel.Information",
            marketHost, StringComparison.Ordinal);
        Assert.Contains("Completed paper scan.", scanner, StringComparison.Ordinal);
    }
}
