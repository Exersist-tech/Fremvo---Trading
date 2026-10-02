namespace Trading.ArchitectureTests;

public sealed class UiNumberFormattingTests
{
    [Fact]
    public void FinancialRenderersLimitOrdinaryDecimalsToTwoPlaces()
    {
        var root = FindRepositoryRoot();
        var files = new[]
        {
            "positions.js",
            "chart.js",
            "scanner-results.js",
            "backtest-results.js",
            "optimization-results.js",
            "experiment-results.js"
        };

        foreach (var file in files)
        {
            var script = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "wwwroot", file));
            Assert.Contains("maximumFractionDigits: 2", script, StringComparison.Ordinal);
        }

        var web = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));
        Assert.Contains("maximumFractionDigits: 2", web, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.abs(n) >= 100 ? 2", web, StringComparison.Ordinal);
    }

    [Fact]
    public void QuantityRenderersRetainUpToEightDecimalPlaces()
    {
        var root = FindRepositoryRoot();
        var files = new[]
        {
            "positions.js",
            "chart.js",
            "portfolio.js",
            "experiment-results.js"
        };

        foreach (var file in files)
        {
            var script = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "wwwroot", file));
            Assert.Contains("maximumFractionDigits: 8", script, StringComparison.Ordinal);
        }

        var backtest = File.ReadAllText(
            Path.Combine(root, "src", "Trading.Web", "wwwroot", "backtest-results.js"));
        Assert.Contains("header.quantity", backtest, StringComparison.Ordinal);
        Assert.Contains("maximumFractionDigits: 8", backtest, StringComparison.Ordinal);

        var web = File.ReadAllText(Path.Combine(root, "src", "Trading.Web", "Program.cs"));
        Assert.Contains("maximumFractionDigits: 8", web, StringComparison.Ordinal);
        Assert.Contains("formatQuantity(o.quantity)", web, StringComparison.Ordinal);
        Assert.Contains("quantityValue(worker.positionQuantity)", web, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trading.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
