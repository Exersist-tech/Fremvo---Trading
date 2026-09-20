using Trading.Backtesting;

namespace Trading.ArchitectureTests;

internal static class DatasetSplitTestFactory
{
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    public static HistoricalDataset Create(
        string symbol = "BTCUSDT",
        string source = "kraken",
        string interval = "1D") =>
        new(
            $"{source}-{symbol}-{interval}",
            source,
            symbol,
            interval,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            1_000,
            Fingerprint,
            "test-v1",
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
}
