using Microsoft.Extensions.Options;
using Trading.Application.Experiments;

namespace Trading.Workers.Experiments;

/// <summary>
/// Explicitly opt-in schedule for unattended protective exits. This is separate from the
/// analysis worker so registering protective protection never activates broader training.
/// </summary>
public sealed class ExperimentProtectiveExitWorkerOptions
{
    public const string SectionName = "Experiments:ProtectiveExits";
    public bool Enabled { get; set; }
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(1);
    public IList<Guid> EnabledUserIds { get; } = new List<Guid>();

    internal TimeSpan BoundedInterval =>
        TickInterval < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) :
        TickInterval > TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(15) : TickInterval;
}

/// <summary>Host-safe default with no position source configured means no exit can be submitted.</summary>
public sealed class UnconfiguredExperimentProtectiveExitPositionSource : IExperimentProtectiveExitPositionSource
{
    public Task<IReadOnlyList<ExperimentProtectiveExitPosition>> ListOpenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ExperimentProtectiveExitPosition>>([]);
    }
}

public sealed class UnconfiguredExperimentProtectiveExitOwnerEvaluator : IExperimentProtectiveExitOwnerEvaluator
{
    public Task<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>> EvaluateOwnerAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>>([]);
    }
}

/// <summary>
/// Paper-only, bounded-cadence protective-exit scheduler. Owner faults are isolated and no
/// exception message is logged, preventing credential-like details from entering telemetry.
/// </summary>
public sealed class ProtectiveExitWorker : BackgroundService
{
    private static readonly Action<ILogger, Guid, Exception?> s_logOwnerFaulted =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(11, "ExperimentProtectiveExitOwnerFaulted"),
            "An experiment protective-exit owner was skipped for this tick. Owner: {OwnerId}.");
    private static readonly Action<ILogger, int, Exception?> s_logTick =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(12, "ExperimentProtectiveExitTickCompleted"),
            "Experiment protective-exit tick completed. Submitted exits: {Submitted}.");

    private readonly ILogger<ProtectiveExitWorker> _logger;
    private readonly IExperimentProtectiveExitOwnerEvaluator _orchestrator;
    private readonly ExperimentProtectiveExitWorkerOptions _options;
    private readonly TimeProvider _timeProvider;

    public ProtectiveExitWorker(
        ILogger<ProtectiveExitWorker> logger,
        IExperimentProtectiveExitOwnerEvaluator orchestrator,
        IOptions<ExperimentProtectiveExitWorkerOptions> options,
        TimeProvider timeProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunTickAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(_options.BoundedInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task<int> RunTickAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return 0;

        var submitted = 0;
        foreach (var owner in _options.EnabledUserIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner == Guid.Empty)
                continue;

            try
            {
                var results = await _orchestrator.EvaluateOwnerAsync(owner, cancellationToken).ConfigureAwait(false);
                submitted += results.Count(result => result.Submitted);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One malformed owner must not stop another paper worker.
            catch (Exception)
#pragma warning restore CA1031
            {
                s_logOwnerFaulted(_logger, owner, null);
            }
        }

        s_logTick(_logger, submitted, null);
        return submitted;
    }
}
