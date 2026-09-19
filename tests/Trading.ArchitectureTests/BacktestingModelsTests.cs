using Trading.Backtesting;

namespace Trading.ArchitectureTests;

public sealed class BacktestingModelsTests
{
    [Fact]
    public void BacktestConfigurationRejectsInvalidWindow()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new BacktestConfiguration(
                "strategy-1",
                "BTCUSDT",
                new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                50,
                10000m,
                0.001m,
                0.0005m));

        Assert.Equal("toUtc", ex.ParamName);
    }

    [Fact]
    public void HistoricalDatasetAndBacktestResultCaptureSafeInputs()
    {
        var dataset = new HistoricalDataset(
            "dataset-1",
            "ETHUSDT",
            "15m",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            1000,
            isImmutable: true);

        var result = new BacktestResult(
            "strategy-1",
            "ETHUSDT",
            dataset.FromUtc,
            dataset.ToUtc,
            10000m,
            10950m,
            950m,
            44m,
            12m,
            18);

        Assert.Equal("ETHUSDT", dataset.Symbol);
        Assert.True(dataset.IsImmutable);
        Assert.Equal(44m, result.TotalFees);
        Assert.Equal(18, result.TradeCount);
    }
}
