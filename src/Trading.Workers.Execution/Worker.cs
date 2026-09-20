namespace Trading.Workers.Execution;

public sealed class Worker : BackgroundService
{
    private static readonly Action<ILogger, DateTimeOffset, Exception?> s_logWorkerRunning =
        LoggerMessage.Define<DateTimeOffset>(
            LogLevel.Information,
            new EventId(1, nameof(Worker)),
            "Worker running at: {Time}");

    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            s_logWorkerRunning(_logger, DateTimeOffset.UtcNow, null);
            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
    }
}
