using Trading.Application.Experiments;
using Trading.Domain.Experiments;

namespace Trading.Workers.Experiments;

/// <summary>Host-safe default: this prerequisite must not start worker trading or analysis.</summary>
public sealed class UnconfiguredExperimentWorkerRunner : IExperimentWorkerRunner
{
    public Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
