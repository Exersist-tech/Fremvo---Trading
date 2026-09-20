using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Execution;

namespace Trading.Workers.Experiments;

/// <summary>
/// Stands in for the market-data feed until ingestion is connected to this host. It reports that
/// no closed candle is available, which leaves workers idle. It deliberately does not invent
/// candles: a fabricated candle would produce fabricated trading results.
/// </summary>
public sealed class UnconfiguredExperimentMarketFeed : IExperimentMarketFeed
{
    private static readonly Action<ILogger, Exception?> s_logNoFeed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(10, "ExperimentMarketFeedNotConfigured"),
            "No experiment market-data feed is configured. Workers will stay idle until one is registered.");

    private readonly ILogger<UnconfiguredExperimentMarketFeed> _logger;
    private int _warned;

    public UnconfiguredExperimentMarketFeed(ILogger<UnconfiguredExperimentMarketFeed> logger)
        => _logger = logger;

    public Task<MarketEvent?> TryGetNextClosedEventAsync(
        Guid workerId,
        string symbol,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            s_logNoFeed(_logger, null);
        }

        return Task.FromResult<MarketEvent?>(null);
    }
}

/// <summary>
/// Stands in for the executable strategy-template registry until approved templates are compiled
/// into runnable form. It resolves nothing, so no strategy can run by accident.
/// </summary>
public sealed class UnconfiguredStrategyTemplateFactory : IApprovedStrategyTemplateFactory
{
    public IPipelineStrategy? TryCreate(string strategyTemplateId, string parametersJson) => null;
}
