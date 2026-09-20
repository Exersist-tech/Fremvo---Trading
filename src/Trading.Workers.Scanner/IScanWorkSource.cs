using Trading.Domain.Market;
using Trading.Domain.Scanner;
using Trading.MarketData;

namespace Trading.Workers.Scanner;

/// <summary>
/// Read-only boundary for explicitly scheduled, owner-scoped scan definitions
/// and their closed-candle evidence. Implementations must apply the supplied
/// owner context to every definition and candle query.
/// </summary>
public interface IScanWorkSource
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<ScheduledScanWork>> ListScheduledAsync(CancellationToken cancellationToken);

    Task<ScanWorkData?> LoadAsync(
        Guid ownerId,
        Guid scanRequestId,
        CancellationToken cancellationToken);
}

/// <summary>
/// An explicit unit of scheduling work. Run identity comes from the durable
/// source so retries use repository idempotency rather than overwrite evidence.
/// </summary>
public sealed record ScheduledScanWork(
    Guid OwnerId,
    Guid ScanRequestId,
    Guid ScanRunId,
    DateTimeOffset EvidenceAsOfUtc);

public sealed record ScanWorkData(
    ScanRequest Request,
    IReadOnlyDictionary<string, IReadOnlyList<Candle>> ClosedCandlesBySymbol);

/// <summary>
/// Safe host default. It deliberately supplies no work and declares that no
/// durable scan/candle source was configured.
/// </summary>
public sealed class UnconfiguredScanWorkSource : IScanWorkSource
{
    public bool IsConfigured => false;

    public Task<IReadOnlyList<ScheduledScanWork>> ListScheduledAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ScheduledScanWork>>(Array.Empty<ScheduledScanWork>());

    public Task<ScanWorkData?> LoadAsync(
        Guid ownerId,
        Guid scanRequestId,
        CancellationToken cancellationToken) =>
        Task.FromResult<ScanWorkData?>(null);
}
