using Trading.Domain.Market;
using Trading.Domain.Universe;

namespace Trading.Application.Universe;

/// <summary>
/// An evidence source used when no measurement store is configured.
/// </summary>
/// <remarks>
/// It reports no metrics and no history rather than fabricating values.
/// Because every eligibility gate fails closed, this yields no grants — the
/// correct outcome for a host that cannot actually measure anything.
/// </remarks>
public sealed class UnconfiguredUniverseEvidenceSource : IUniverseEvidenceSource
{
    public Task<InstrumentMetrics?> GetMetricsAsync(
        Guid instrumentId,
        CandleInterval interval,
        CancellationToken cancellationToken) => Task.FromResult<InstrumentMetrics?>(null);

    public Task<int> GetAvailableHistoryCandlesAsync(
        Guid instrumentId,
        CandleInterval interval,
        CancellationToken cancellationToken) => Task.FromResult(0);
}
