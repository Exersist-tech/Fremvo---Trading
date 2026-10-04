namespace Trading.Application.Reporting;

public interface IPaperTransactionReportStore
{
    Task StoreAsync(Guid ownerId, Guid reportId, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadAsync(Guid ownerId, Guid reportId,
        CancellationToken cancellationToken = default);
}
