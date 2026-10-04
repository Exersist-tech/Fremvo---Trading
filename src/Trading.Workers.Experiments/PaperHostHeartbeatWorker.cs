using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.Workers.Experiments;

public sealed class PaperHostHeartbeatWorker(
    IServiceScopeFactory scopes, TimeProvider time, ILogger<PaperHostHeartbeatWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> s_logHeartbeat =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, "PaperExperimentHeartbeat"),
            "Paper experiment host heartbeat persisted.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await new EfPaperHostHeartbeatRepository(db)
                .RecordAsync(EfPaperHostHeartbeatRepository.Experiments, time.GetUtcNow(), stoppingToken)
                .ConfigureAwait(false);
            s_logHeartbeat(logger, null);
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken).ConfigureAwait(false);
        }
    }
}
