using Trading.Application.Experiments;
using Trading.Domain.Experiments;

namespace Trading.ArchitectureTests;

public sealed class ExperimentWorkerPoolTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ExperimentWorkerPool Pool(out InMemoryExperimentWorkerRepository repository)
    {
        repository = new InMemoryExperimentWorkerRepository();
        return new ExperimentWorkerPool(repository);
    }

    private sealed class AlwaysFaultsRunner : IExperimentWorkerRunner
    {
        public Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
            => throw new InvalidOperationException("secret-connection-string-should-not-leak");
    }

    private sealed class FaultsOneRunner : IExperimentWorkerRunner
    {
        private readonly Guid _faultingWorkerId;

        public FaultsOneRunner(Guid faultingWorkerId) => _faultingWorkerId = faultingWorkerId;

        public List<Guid> Ran { get; } = new();

        public Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
        {
            if (worker.Id == _faultingWorkerId)
            {
                throw new InvalidOperationException("boom");
            }

            Ran.Add(worker.Id);
            worker.ApplyPaperTrade(1m, 100m, 0.1m, "buy");
            return Task.CompletedTask;
        }
    }

    private static async Task<ExperimentWorker> CreateRunningAsync(
        ExperimentWorkerPool pool,
        InMemoryExperimentWorkerRepository repository,
        Guid userId,
        string name,
        int seed)
    {
        var worker = await pool.CreateWorkerAsync(userId, name, "template-sma", "BTCUSDT", 10_000m, Now, seed);
        worker.Start();
        await repository.SaveAsync(worker);
        return worker;
    }

    [Fact]
    public async Task AUserCannotExceedTenWorkers()
    {
        var pool = Pool(out var repository);
        var userId = Guid.NewGuid();

        for (var i = 0; i < ExperimentWorker.MaxWorkersPerUser; i++)
        {
            await pool.CreateWorkerAsync(userId, $"w{i}", "template-sma", "BTCUSDT", 1_000m, Now, i);
        }

        Assert.Equal(10, await repository.CountAsync(userId));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => pool.CreateWorkerAsync(userId, "w11", "template-sma", "BTCUSDT", 1_000m, Now, 11));
    }

    [Fact]
    public async Task OneUsersWorkerCountDoesNotLimitAnotherUser()
    {
        var pool = Pool(out var repository);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        for (var i = 0; i < ExperimentWorker.MaxWorkersPerUser; i++)
        {
            await pool.CreateWorkerAsync(first, $"w{i}", "template-sma", "BTCUSDT", 1_000m, Now, i);
        }

        var other = await pool.CreateWorkerAsync(second, "mine", "template-sma", "BTCUSDT", 1_000m, Now, 1);

        Assert.Equal(second, other.UserId);
        Assert.Equal(1, await repository.CountAsync(second));
    }

    [Fact]
    public async Task AFaultingWorkerDoesNotStopTheOtherWorkers()
    {
        var pool = Pool(out var repository);
        var userId = Guid.NewGuid();

        var a = await CreateRunningAsync(pool, repository, userId, "a", 1);
        var b = await CreateRunningAsync(pool, repository, userId, "b", 2);
        var c = await CreateRunningAsync(pool, repository, userId, "c", 3);

        var runner = new FaultsOneRunner(b.Id);
        var result = await pool.RunAllAsync(userId, runner);

        Assert.Equal(1, result.FaultedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Contains(a.Id, runner.Ran);
        Assert.Contains(c.Id, runner.Ran);
        Assert.Equal(ExperimentWorkerStatus.Failed, b.Status);
        Assert.Equal(ExperimentWorkerStatus.Running, a.Status);
        Assert.Equal(ExperimentWorkerStatus.Running, c.Status);
    }

    [Fact]
    public async Task AWorkerFaultReasonNeverCarriesTheExceptionMessage()
    {
        var pool = Pool(out var repository);
        var userId = Guid.NewGuid();
        var worker = await CreateRunningAsync(pool, repository, userId, "a", 1);

        var result = await pool.RunAllAsync(userId, new AlwaysFaultsRunner());

        var reason = Assert.Single(result.Results).FailureReason;
        Assert.NotNull(reason);
        Assert.DoesNotContain("secret-connection-string", reason, StringComparison.Ordinal);
        Assert.Equal(worker.FailureReason, reason);
    }

    [Fact]
    public async Task WorkersKeepSeparateBalancesAndPositions()
    {
        var pool = Pool(out var repository);
        var userId = Guid.NewGuid();

        var a = await CreateRunningAsync(pool, repository, userId, "a", 1);
        var b = await CreateRunningAsync(pool, repository, userId, "b", 2);

        a.ApplyPaperTrade(2m, 100m, 0m, "buy");

        Assert.Equal(2m, a.PositionQuantity);
        Assert.Equal(0m, b.PositionQuantity);
        Assert.Equal(9_800m, a.CashBalance);
        Assert.Equal(10_000m, b.CashBalance);
    }

    [Fact]
    public async Task AWorkerBelongingToAnotherUserReadsAsNotFound()
    {
        var pool = Pool(out var repository);
        var owner = Guid.NewGuid();
        var intruder = Guid.NewGuid();

        var worker = await pool.CreateWorkerAsync(owner, "a", "template-sma", "BTCUSDT", 1_000m, Now, 1);

        Assert.NotNull(await repository.GetAsync(owner, worker.Id));
        Assert.Null(await repository.GetAsync(intruder, worker.Id));
    }

    [Fact]
    public async Task PausedWorkersAreSkippedRatherThanRun()
    {
        var pool = Pool(out var repository);
        var userId = Guid.NewGuid();

        var worker = await CreateRunningAsync(pool, repository, userId, "a", 1);
        worker.Pause();
        await repository.SaveAsync(worker);

        var result = await pool.RunAllAsync(userId, new AlwaysFaultsRunner());

        Assert.Equal(ExperimentWorkerOutcome.Skipped, Assert.Single(result.Results).Outcome);
        Assert.Equal(ExperimentWorkerStatus.Paused, worker.Status);
    }

    [Fact]
    public async Task ARandomSeedIsFixedAtConstructionSoARunIsReproducible()
    {
        var pool = Pool(out _);
        var userId = Guid.NewGuid();

        var a = await pool.CreateWorkerAsync(userId, "a", "template-sma", "BTCUSDT", 1_000m, Now, 4242);
        var b = await pool.CreateWorkerAsync(userId, "b", "template-sma", "BTCUSDT", 1_000m, Now, 99);

        Assert.Equal(4242, a.RandomSeed);
        Assert.Equal(99, b.RandomSeed);
        Assert.NotEqual(a.RandomSeed, b.RandomSeed);
    }
}
