using Trading.Application.Experiments;

namespace Trading.Workers.MarketData;

public sealed class ContinuousPaperScannerWorker : BackgroundService
{
    internal static readonly TimeSpan RestSettlementGrace = TimeSpan.FromSeconds(30);
    private static readonly Action<ILogger, Guid, string, Exception?> s_logFault =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(1, "ContinuousPaperScanFaulted"),
            "Continuous paper scan failed safely. Owner={OwnerId} ErrorType={ErrorType}.");
    private static readonly Action<ILogger, Guid, int, int, int, int, Exception?> s_logScan =
        LoggerMessage.Define<Guid, int, int, int, int>(
            LogLevel.Information,
            new EventId(2, "ContinuousPaperScanCompleted"),
            "Completed paper scan. Owner={OwnerId} EligiblePairs={EligiblePairs} Evaluated={Evaluated} Qualified={Qualified} Admitted={Admitted}.");

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<ContinuousPaperScannerWorker> _logger;

    public ContinuousPaperScannerWorker(
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<ContinuousPaperScannerWorker> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (BoundaryHasSettled(_time.GetUtcNow()))
            {
                using var scope = _scopes.CreateScope();
                var activations = scope.ServiceProvider.GetRequiredService<IPaperTrainingActivationSource>();
                var owners = await activations.GetActiveOwnerIdsAsync(stoppingToken).ConfigureAwait(false);
                foreach (var ownerId in owners)
                {
                    if (!BoundaryHasSettled(_time.GetUtcNow()))
                        break;
                    try
                    {
                        var result = await scope.ServiceProvider
                            .GetRequiredService<ContinuousPaperOpportunityScanner>()
                            .ScanAsync(ownerId, stoppingToken)
                            .ConfigureAwait(false);
                        if (result.Persisted)
                            s_logScan(_logger, ownerId, result.EligiblePairs,
                                result.EvaluatedCandidates, result.QualifiedCandidates,
                                result.AdmittedCandidates, null);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
#pragma warning disable CA1031 // One owner's public-data scan must not stop another owner's scan.
                    catch (Exception exception)
#pragma warning restore CA1031
                    {
                        s_logFault(_logger, ownerId, exception.GetType().Name, null);
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal static bool BoundaryHasSettled(DateTimeOffset nowUtc)
    {
        var utc = nowUtc.ToUniversalTime();
        var interval = TimeSpan.FromMinutes(5).Ticks;
        var intoBoundary = utc.Ticks % interval;
        return intoBoundary >= RestSettlementGrace.Ticks;
    }
}
