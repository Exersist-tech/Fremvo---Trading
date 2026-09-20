using Trading.Domain.Scanner;

namespace Trading.Application.Scanner;

/// <summary>
/// Owner-scoped durable storage for immutable scan definitions and their
/// informational results. No repository operation can list another owner's data.
/// </summary>
public interface IScanRequestRepository
{
    Task AddAsync(Guid ownerId, ScanRequest request, CancellationToken cancellationToken = default);

    Task<ScanRequest?> GetAsync(Guid ownerId, Guid scanRequestId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanRequest>> ListAsync(
        Guid ownerId,
        int limit,
        CancellationToken cancellationToken = default);
}

public interface IScanResultRepository
{
    Task<ScanResultWriteResult> RecordAsync(
        Guid ownerId,
        ScanResult result,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanResult>> ListAsync(
        Guid ownerId,
        Guid scanRequestId,
        Guid scanRunId,
        int limit,
        CancellationToken cancellationToken = default);
}

public enum ScanResultWriteResult
{
    Inserted,
    Duplicate,
    Conflict
}
