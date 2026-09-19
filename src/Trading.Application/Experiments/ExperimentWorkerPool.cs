using Trading.Domain.Experiments;

namespace Trading.Application.Experiments;

/// <summary>
/// Runs a single experiment worker for one slice of work. Implementations own the strategy
/// evaluation and paper execution for that worker only; they must never touch another worker's
/// state, and must never reach a live exchange.
/// </summary>
public interface IExperimentWorkerRunner
{
    Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken);
}

public enum ExperimentWorkerOutcome
{
    Completed = 0,

    /// <summary>The worker faulted. The other workers in the pool are unaffected.</summary>
    Faulted,

    /// <summary>The worker was skipped because it was not in a runnable state.</summary>
    Skipped
}

public sealed class ExperimentWorkerRunResult
{
    public ExperimentWorkerRunResult(Guid workerId, ExperimentWorkerOutcome outcome, string? failureReason)
    {
        WorkerId = workerId;
        Outcome = outcome;
        FailureReason = failureReason;
    }

    public Guid WorkerId { get; }

    public ExperimentWorkerOutcome Outcome { get; }

    public string? FailureReason { get; }
}

public sealed class ExperimentWorkerPoolResult
{
    public ExperimentWorkerPoolResult(IReadOnlyList<ExperimentWorkerRunResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        Results = results;
    }

    public IReadOnlyList<ExperimentWorkerRunResult> Results { get; }

    public int FaultedCount => Results.Count(r => r.Outcome == ExperimentWorkerOutcome.Faulted);

    public int CompletedCount => Results.Count(r => r.Outcome == ExperimentWorkerOutcome.Completed);
}

/// <summary>
/// Coordinates up to <see cref="ExperimentWorker.MaxWorkersPerUser"/> isolated workers for one user.
/// A fault in one worker is captured and recorded against that worker alone; the remaining workers
/// still run. No state is shared between workers other than immutable historical market data.
/// </summary>
public sealed class ExperimentWorkerPool
{
    private readonly IExperimentWorkerRepository _repository;

    public ExperimentWorkerPool(IExperimentWorkerRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<ExperimentWorker> CreateWorkerAsync(
        Guid userId,
        string name,
        string strategyId,
        string marketSymbol,
        decimal startingCash,
        DateTimeOffset createdAtUtc,
        int randomSeed,
        CancellationToken cancellationToken = default)
    {
        var existing = await _repository.CountAsync(userId, cancellationToken).ConfigureAwait(false);

        if (!ExperimentWorker.CanCreateMoreWorkers(existing))
        {
            throw new InvalidOperationException(
                $"A user may run at most {ExperimentWorker.MaxWorkersPerUser} experiment workers.");
        }

        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            userId,
            name,
            strategyId,
            marketSymbol,
            startingCash,
            createdAtUtc,
            randomSeed);

        await _repository.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
        return worker;
    }

    public async Task<ExperimentWorkerPoolResult> RunAllAsync(
        Guid userId,
        IExperimentWorkerRunner runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);

        var workers = await _repository.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        var results = new List<ExperimentWorkerRunResult>(workers.Count);

        foreach (var worker in workers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (worker.Status != ExperimentWorkerStatus.Running)
            {
                results.Add(new ExperimentWorkerRunResult(worker.Id, ExperimentWorkerOutcome.Skipped, null));
                continue;
            }

            try
            {
                await runner.RunOnceAsync(worker, cancellationToken).ConfigureAwait(false);
                results.Add(new ExperimentWorkerRunResult(worker.Id, ExperimentWorkerOutcome.Completed, null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // A faulting worker must be contained, not allowed to stop the other nine.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Only the exception type is recorded, never the message, so a worker fault cannot
                // leak credentials or connection strings into operator-visible state.
                worker.Fail($"Experiment worker faulted: {ex.GetType().Name}");
                await _repository.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
                results.Add(new ExperimentWorkerRunResult(
                    worker.Id,
                    ExperimentWorkerOutcome.Faulted,
                    worker.FailureReason));
            }
        }

        return new ExperimentWorkerPoolResult(results);
    }
}

/// <summary>
/// Non-durable worker store for paper experiments. Each user's workers are kept in a separate
/// bucket so no query can cross a user boundary.
/// </summary>
public sealed class InMemoryExperimentWorkerRepository : IExperimentWorkerRepository
{
    private readonly Dictionary<Guid, Dictionary<Guid, ExperimentWorker>> _byUser = new();

    public Task<int> CountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_byUser.TryGetValue(userId, out var workers) ? workers.Count : 0);
    }

    public Task<ExperimentWorker?> GetAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_byUser.TryGetValue(userId, out var workers) && workers.TryGetValue(workerId, out var worker))
        {
            return Task.FromResult<ExperimentWorker?>(worker);
        }

        return Task.FromResult<ExperimentWorker?>(null);
    }

    public Task<IReadOnlyCollection<ExperimentWorker>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyCollection<ExperimentWorker> result = _byUser.TryGetValue(userId, out var workers)
            ? workers.Values.ToList()
            : Array.Empty<ExperimentWorker>();

        return Task.FromResult(result);
    }

    public Task SaveAsync(ExperimentWorker worker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_byUser.TryGetValue(worker.UserId, out var workers))
        {
            workers = new Dictionary<Guid, ExperimentWorker>();
            _byUser[worker.UserId] = workers;
        }

        workers[worker.Id] = worker;
        return Task.CompletedTask;
    }
}
