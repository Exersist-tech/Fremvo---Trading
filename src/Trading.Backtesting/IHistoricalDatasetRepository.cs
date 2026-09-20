namespace Trading.Backtesting;

/// <summary>
/// Durable storage for platform-owned immutable historical dataset manifests.
/// </summary>
public interface IHistoricalDatasetRepository
{
    Task<HistoricalDatasetWriteResult> StoreAsync(
        HistoricalDataset dataset,
        CancellationToken cancellationToken = default);

    Task<HistoricalDataset?> GetAsync(
        string versionIdentity,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HistoricalDataset>> ListAsync(
        string symbol,
        string interval,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int limit,
        CancellationToken cancellationToken = default);
}

public enum HistoricalDatasetWriteResult
{
    Inserted,
    Duplicate,
    Conflict
}
