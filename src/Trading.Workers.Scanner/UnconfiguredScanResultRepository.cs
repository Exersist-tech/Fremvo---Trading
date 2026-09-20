using Trading.Application.Scanner;
using Trading.Domain.Scanner;

namespace Trading.Workers.Scanner;

/// <summary>
/// Prevents an accidentally enabled host from treating missing durable result
/// storage as successful persistence.
/// </summary>
public sealed class UnconfiguredScanResultRepository : IScanResultRepository
{
    public Task<ScanResultWriteResult> RecordAsync(
        Guid ownerId,
        ScanResult result,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ScanResultWriteResult>(
            new InvalidOperationException("No durable scan result repository is configured."));

    public Task<IReadOnlyList<ScanResult>> ListAsync(
        Guid ownerId,
        Guid scanRequestId,
        Guid scanRunId,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ScanResult>>(
            new InvalidOperationException("No durable scan result repository is configured."));
}
