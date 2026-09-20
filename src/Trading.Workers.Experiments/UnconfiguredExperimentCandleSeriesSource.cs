using Trading.MarketData.Experiments;

namespace Trading.Workers.Experiments;

/// <summary>
/// Default experiment-training data source. It deliberately exposes no candle until a future
/// training configuration explicitly selects a durable dataset.
/// </summary>
public sealed class UnconfiguredExperimentCandleSeriesSource : IExperimentCandleSeriesSource
{
    public Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
        ExperimentCandleSeriesRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.NoData));
    }
}
