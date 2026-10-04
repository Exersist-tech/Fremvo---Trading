using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Trading.Application.Execution;
using Trading.Domain.Execution;
using Trading.Exchanges.Kraken.Execution;

namespace Trading.Web;

/// <summary>Periodically reconciles unresolved live orders without submitting orders.</summary>
public sealed class LiveReconciliationWorker(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<LiveReconciliationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly Action<ILogger, string, Exception?> QueueUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning,
            new EventId(9120, "LiveReconciliationQueueUnavailable"),
            "Live reconciliation cannot read the pending order queue ({ErrorType}).");
    private static readonly Action<ILogger, string, Exception?> OwnerUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning,
            new EventId(9121, "LiveReconciliationOwnerUnavailable"),
            "Live reconciliation left the owner's orders frozen ({ErrorType}).");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is SqlException or DbUpdateException
                    or InvalidOperationException)
                {
                    QueueUnavailable(logger, error.GetType().Name, null);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<IOrderReconciliationRepository>()
            .ListUnresolvedAsync(cancellationToken).ConfigureAwait(false);
        var orders = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var pendingByOwner = new Dictionary<Guid, List<Guid>>();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var order = await orders.FindByOrderIdForReconciliationAsync(record.OrderId, cancellationToken)
                .ConfigureAwait(false);
            if (order is null || order.Mode != TradingMode.Live || !order.RequiresReconciliation)
                continue;
            if (!pendingByOwner.TryGetValue(order.UserId, out var ids))
                pendingByOwner.Add(order.UserId, ids = []);
            ids.Add(record.Id);
        }

        foreach (var (ownerId, recordIds) in pendingByOwner)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var ownerScope = scopes.CreateScope();
            try
            {
                await ownerScope.ServiceProvider.GetRequiredService<LiveOrderSyncService>()
                    .SyncAsync(ownerId, cancellationToken).ConfigureAwait(false);
                var repository = ownerScope.ServiceProvider.GetRequiredService<IOrderReconciliationRepository>();
                var reconciliation = ownerScope.ServiceProvider.GetRequiredService<OrderReconciliationService>();
                foreach (var recordId in recordIds)
                {
                    var record = await repository.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
                    if (record is not null && !record.IsResolved)
                        await reconciliation.ResolveAsync(record, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is InvalidDataException or SqlException
                or DbUpdateException or KrakenTransportException or HttpRequestException
                or InvalidOperationException)
            {
                OwnerUnavailable(logger, error.GetType().Name, null);
            }
        }
    }
}
