using Trading.Application.Execution;

namespace Trading.ArchitectureTests;

internal sealed class PassThroughLiveFillTransaction : ILiveFillPersistenceTransaction
{
    public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        operation(cancellationToken);
}
