using Microsoft.Extensions.Options;
using Trading.Application.Experiments;

namespace Trading.Workers.Experiments;

/// <summary>
/// Advances every enabled user's experiment worker pool on a fixed interval. The host never
/// aborts because of a worker fault: <see cref="ExperimentWorkerPool"/> contains each fault to the
/// worker that caused it, and a fault in one user's pool does not stop another user's pool.
/// </summary>
public sealed class Worker : BackgroundService
{
    private static readonly Action<ILogger, int, int, Exception?> s_logTickCompleted =
        LoggerMessage.Define<int, int>(
            LogLevel.Information,
            new EventId(1, "ExperimentTickCompleted"),
            "Experiment tick completed. Workers advanced: {Completed}. Workers faulted: {Faulted}.");

    private static readonly Action<ILogger, Guid, Exception?> s_logPoolFaulted =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(2, "ExperimentPoolFaulted"),
            "An experiment pool faulted and was skipped for this tick. Owner: {OwnerId}.");

    private readonly ILogger<Worker> _logger;
    private readonly ExperimentWorkerPool _pool;
    private readonly IExperimentWorkerRunner _runner;
    private readonly IExperimentResearchGroupConfigurationSource _configurationSource;
    private readonly ExperimentHostOptions _options;
    private readonly TimeProvider _timeProvider;

    public Worker(
        ILogger<Worker> logger,
        ExperimentWorkerPool pool,
        IExperimentWorkerRunner runner,
        IExperimentResearchGroupConfigurationSource configurationSource,
        IOptions<ExperimentHostOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _pool = pool;
        _runner = runner;
        _configurationSource = configurationSource;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunTickAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(_options.TickInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task<int> RunTickAsync(CancellationToken cancellationToken)
    {
        var completed = 0;
        var faulted = 0;

        foreach (var userId in _options.EnabledUserIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var configuration = await _configurationSource.GetAsync(userId, cancellationToken).ConfigureAwait(false);
                var result = await _pool.RunConfiguredAsync(userId, configuration, _runner, cancellationToken).ConfigureAwait(false);
                completed += result.CompletedCount;
                faulted += result.FaultedCount;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One user's pool must never stop another user's pool.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Only the surrogate owner id is logged. No exception detail is emitted so a fault
                // can never leak credentials or connection strings into telemetry.
                s_logPoolFaulted(_logger, userId, null);
                faulted++;
            }
        }

        s_logTickCompleted(_logger, completed, faulted, null);
        return faulted;
    }
}
