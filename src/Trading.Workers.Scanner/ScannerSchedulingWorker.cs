using Trading.Application.Scanner;

namespace Trading.Workers.Scanner;

public enum ScannerPassStatus
{
    Disabled,
    UnconfiguredSource,
    Completed,
    Failed
}

public sealed record ScannerPassReport(
    ScannerPassStatus Status,
    int Scheduled,
    int Completed,
    int NoResults,
    int Failed,
    int Inserted,
    int Duplicates,
    int Conflicts);

/// <summary>
/// Schedules informational, owner-isolated scan evaluation. This worker only
/// reads supplied closed-candle evidence and writes existing ScanResult records.
/// It neither fetches market data nor creates trading actions.
/// </summary>
public sealed class ScannerSchedulingWorker : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> s_logUnavailable =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(10, nameof(ScannerSchedulingWorker)),
            "Scanner scheduling did not run: {Category}.");

    private static readonly Action<ILogger, Guid, Guid, string, Exception?> s_logRequestFailed =
        LoggerMessage.Define<Guid, Guid, string>(
            LogLevel.Warning,
            new EventId(11, nameof(ScannerSchedulingWorker)),
            "Scanner request {RequestId} in run {RunId} failed: {Category}.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> s_logConflict =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(12, nameof(ScannerSchedulingWorker)),
            "Scanner request {RequestId} in run {RunId} had a result conflict; existing evidence was retained.");

    private readonly ILogger<ScannerSchedulingWorker> _logger;
    private readonly IScanWorkSource _workSource;
    private readonly IScanResultRepository _results;
    private readonly ScannerSchedulingOptions _options;
    private readonly TimeProvider _timeProvider;

    public ScannerSchedulingWorker(
        ILogger<ScannerSchedulingWorker> logger,
        IScanWorkSource workSource,
        IScanResultRepository results,
        ScannerSchedulingOptions options,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _workSource = workSource;
        _results = results;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<ScannerPassReport> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.Enabled)
        {
            s_logUnavailable(_logger, "disabled", null);
            return Empty(ScannerPassStatus.Disabled);
        }

        try
        {
            _options.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            s_logUnavailable(_logger, "invalid-configuration", null);
            return Empty(ScannerPassStatus.Failed);
        }

        if (!_workSource.IsConfigured)
        {
            s_logUnavailable(_logger, "unconfigured-source", null);
            return Empty(ScannerPassStatus.UnconfiguredSource);
        }

        IReadOnlyList<ScheduledScanWork> work;
        try
        {
            work = await _workSource.ListScheduledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Scheduling must fail closed without one source failure terminating future passes.
        catch (Exception)
#pragma warning restore CA1031
        {
            s_logUnavailable(_logger, "work-list-failed", null);
            return Empty(ScannerPassStatus.Failed);
        }

        var completed = 0;
        var noResults = 0;
        var failed = 0;
        var inserted = 0;
        var duplicates = 0;
        var conflicts = 0;
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ExecuteAsync(item, cancellationToken).ConfigureAwait(false);
                completed++;
                noResults += outcome.NoResults;
                inserted += outcome.Inserted;
                duplicates += outcome.Duplicates;
                conflicts += outcome.Conflicts;
                if (outcome.Conflicts > 0)
                {
                    s_logConflict(_logger, item.ScanRequestId, item.ScanRunId, null);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // Independent scan failures must not prevent other owner-scoped work.
            catch (Exception)
#pragma warning restore CA1031
            {
                failed++;
                s_logRequestFailed(_logger, item.ScanRequestId, item.ScanRunId, "request-failed", null);
            }
        }

        return new ScannerPassReport(
            ScannerPassStatus.Completed, work.Count, completed, noResults, failed, inserted, duplicates, conflicts);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            s_logUnavailable(_logger, "disabled", null);
            return;
        }

        try
        {
            _options.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            s_logUnavailable(_logger, "invalid-configuration", null);
            return;
        }

        using var timer = new PeriodicTimer(_options.Cadence, _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<ExecutionOutcome> ExecuteAsync(ScheduledScanWork item, CancellationToken cancellationToken)
    {
        Validate(item);
        var data = await _workSource
            .LoadAsync(item.OwnerId, item.ScanRequestId, cancellationToken)
            .ConfigureAwait(false);
        if (data is null ||
            data.Request.OwnerId != item.OwnerId ||
            data.Request.Id != item.ScanRequestId)
        {
            throw new InvalidOperationException("The owner-scoped scan request was unavailable or mismatched.");
        }

        var evaluation = ScanCriteriaEvaluator.Evaluate(
            data.Request,
            item.ScanRunId,
            item.EvidenceAsOfUtc,
            _timeProvider.GetUtcNow(),
            data.ClosedCandlesBySymbol);
        if (evaluation.Results.Count == 0)
        {
            return new ExecutionOutcome(1, 0, 0, 0);
        }

        var inserted = 0;
        var duplicates = 0;
        var conflicts = 0;
        foreach (var result in evaluation.Results)
        {
            var write = await _results.RecordAsync(item.OwnerId, result, cancellationToken).ConfigureAwait(false);
            switch (write)
            {
                case ScanResultWriteResult.Inserted:
                    inserted++;
                    break;
                case ScanResultWriteResult.Duplicate:
                    duplicates++;
                    break;
                case ScanResultWriteResult.Conflict:
                    conflicts++;
                    break;
                default:
                    throw new InvalidOperationException("Scan result repository returned an unknown write outcome.");
            }
        }

        return new ExecutionOutcome(0, inserted, duplicates, conflicts);
    }

    private static void Validate(ScheduledScanWork item)
    {
        if (item.OwnerId == Guid.Empty || item.ScanRequestId == Guid.Empty || item.ScanRunId == Guid.Empty ||
            item.EvidenceAsOfUtc == default)
        {
            throw new ArgumentException("Scheduled scan work is missing owner, request, run, or evidence identity.");
        }
    }

    private static ScannerPassReport Empty(ScannerPassStatus status) =>
        new(status, 0, 0, 0, 0, 0, 0, 0);

    private sealed record ExecutionOutcome(int NoResults, int Inserted, int Duplicates, int Conflicts);
}
