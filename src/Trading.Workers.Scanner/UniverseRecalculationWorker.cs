using Trading.Application.Universe;

namespace Trading.Workers.Scanner;

public sealed class UniverseRecalculationOptions
{
    /// <summary>
    /// How often a full recalculation runs. Recalculation is what makes
    /// eligibility decay, so a long interval means stale evidence keeps
    /// conferring capability for longer.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Runs instrument eligibility recalculation on a schedule.
/// </summary>
/// <remarks>
/// Recalculation is a safety control, not a convenience: it is the mechanism
/// by which an instrument that has stopped meeting its gates loses its grant
/// without anyone having to notice.
///
/// A failed pass never terminates the worker, and never leaves a grant
/// standing on evidence it could not verify — the service revokes on failure.
/// </remarks>
public sealed class UniverseRecalculationWorker : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, Exception?> s_logPass =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Information,
            new EventId(1, nameof(UniverseRecalculationWorker)),
            "Universe recalculation: {Granted} granted, {Revoked} revoked, {Failed} failed.");

    private static readonly Action<ILogger, Exception?> s_logEmpty =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2, nameof(UniverseRecalculationWorker)),
            "Universe recalculation found no instruments; no eligibility was granted.");

    private static readonly Action<ILogger, Exception?> s_logFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(3, nameof(UniverseRecalculationWorker)),
            "Universe recalculation pass failed; eligibility was not refreshed.");

    private readonly ILogger<UniverseRecalculationWorker> _logger;
    private readonly IUniverseWorkSource _workSource;
    private readonly UniverseRecalculationService _service;
    private readonly UniverseRecalculationOptions _options;
    private readonly TimeProvider _timeProvider;

    public UniverseRecalculationWorker(
        ILogger<UniverseRecalculationWorker> logger,
        IUniverseWorkSource workSource,
        UniverseRecalculationService service,
        UniverseRecalculationOptions options,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _workSource = workSource;
        _service = service;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Runs one pass. Exposed so a test can drive a single deterministic pass
    /// without a timer.
    /// </summary>
    public async Task<RecalculationReport?> RunOnceAsync(CancellationToken cancellationToken)
    {
        var work = await _workSource.GetWorkAsync(cancellationToken).ConfigureAwait(false);

        if (work.Instruments.Count == 0 || work.Scopes.Count == 0)
        {
            s_logEmpty(_logger, null);
            return null;
        }

        var report = await _service
            .RecalculateAsync(work.Instruments, work.Eligibility, work.Scopes, cancellationToken)
            .ConfigureAwait(false);

        await _workSource.SaveAsync(report, cancellationToken).ConfigureAwait(false);

        s_logPass(
            _logger,
            report.CountOf(RecalculationOutcome.Granted),
            report.CountOf(RecalculationOutcome.Revoked),
            report.CountOf(RecalculationOutcome.Failed),
            null);

        return report;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
#pragma warning disable CA1031 // A failed pass must never stop the schedule.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                s_logFailed(_logger, exception);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }
}
