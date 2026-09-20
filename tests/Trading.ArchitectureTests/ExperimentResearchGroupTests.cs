using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.Strategies;
using Trading.Strategies.Approvals;
using System.Security.Cryptography;
using System.Text;

namespace Trading.ArchitectureTests;

public sealed class ExperimentResearchGroupTests
{
    private const string Fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstrumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class RecordingRunner : IExperimentWorkerRunner
    {
        public List<Guid> Ran { get; } = new();
        public Guid? FaultWorkerId { get; init; }

        public Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
        {
            if (worker.Id == FaultWorkerId)
            {
                throw new InvalidOperationException("contained");
            }

            Ran.Add(worker.Id);
            worker.ApplyPaperTrade(1m, 100m, 0m, "buy");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TenWorkersAreDeterministicallyAssignedAcrossABCWithIndependentSeedsAndState()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var pool = new ExperimentWorkerPool(repository);
        var userId = Guid.NewGuid();
        var workers = await WorkersAsync(repository, userId, 10);
        var provenance = Provenance(approved: true);

        var first = ExperimentResearchGroupConfiguration.Create(userId, 3, workers.AsEnumerable().Reverse(), provenance);
        var replay = ExperimentResearchGroupConfiguration.Create(userId, 3, workers, provenance);

        Assert.Equal(4, first.Assignments.Count(x => x.Group == ExperimentResearchGroup.A));
        Assert.Equal(3, first.Assignments.Count(x => x.Group == ExperimentResearchGroup.B));
        Assert.Equal(3, first.Assignments.Count(x => x.Group == ExperimentResearchGroup.C));
        Assert.Equal(
            first.Assignments.Select(x => (x.WorkerId, x.Group, x.RandomSeed)),
            replay.Assignments.Select(x => (x.WorkerId, x.Group, x.RandomSeed)));
        Assert.Equal(10, first.Assignments.Select(x => x.RandomSeed).Distinct().Count());
#pragma warning disable CA5394 // These are deterministic, non-security research streams.
        Assert.Equal(first.Assignments[0].CreateRandom().Next(), replay.Assignments[0].CreateRandom().Next());
#pragma warning restore CA5394

        var runner = new RecordingRunner();
        var result = await pool.RunConfiguredAsync(userId, first, runner);

        Assert.Equal(10, result.CompletedCount);
        Assert.All(workers, worker => Assert.Single(worker.Ledger));
        Assert.All(workers, worker => Assert.Equal(9_900m, worker.CashBalance));
        Assert.All(workers, worker => Assert.Equal(1m, worker.PositionQuantity));
    }

    [Fact]
    public async Task GroupConfigurationIsOwnerScopedAndOneFaultDoesNotStopOtherGroups()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var pool = new ExperimentWorkerPool(repository);
        var owner = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var ownerWorkers = await WorkersAsync(repository, owner, 3);
        var otherWorkers = await WorkersAsync(repository, otherUser, 1);
        var configuration = ExperimentResearchGroupConfiguration.Create(owner, 1, ownerWorkers, Provenance(approved: true));

        var wrongOwner = await pool.RunConfiguredAsync(otherUser, configuration, new RecordingRunner());
        Assert.Empty(wrongOwner.Results);
        Assert.Empty(otherWorkers[0].Ledger);

        var runner = new RecordingRunner { FaultWorkerId = ownerWorkers[1].Id };
        var result = await pool.RunConfiguredAsync(owner, configuration, runner);

        Assert.Equal(1, result.FaultedCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(ExperimentWorkerStatus.Failed, ownerWorkers[1].Status);
        Assert.Equal(2, runner.Ran.Count);
        Assert.All(otherWorkers, worker => Assert.Empty(worker.Ledger));
    }

    [Fact]
    public async Task MissingOrUnapprovedEvidenceFailsClosedWithoutRunningOrPromotingAnything()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var pool = new ExperimentWorkerPool(repository);
        var userId = Guid.NewGuid();
        var workers = await WorkersAsync(repository, userId, 1);
        var runner = new RecordingRunner();

        var absent = await pool.RunConfiguredAsync(userId, null, runner);
        var unapproved = ExperimentResearchGroupConfiguration.Create(userId, 1, workers, Provenance(approved: false));
        var rejected = await pool.RunConfiguredAsync(userId, unapproved, runner);

        Assert.Empty(absent.Results);
        Assert.Empty(rejected.Results);
        Assert.Empty(runner.Ran);
        Assert.Empty(workers[0].Ledger);
        Assert.Equal(ExperimentWorkerStatus.Running, workers[0].Status);
    }

    [Fact]
    public async Task ParameterOrDatasetMismatchFailsClosedAndProfitCannotChangeMembership()
    {
        var repository = new InMemoryExperimentWorkerRepository();
        var pool = new ExperimentWorkerPool(repository);
        var userId = Guid.NewGuid();
        var workers = await WorkersAsync(repository, userId, 2);
        var provenance = Provenance(approved: true);
        var beforeProfit = ExperimentResearchGroupConfiguration.Create(userId, 2, workers, provenance);

        workers[0].ApplyPaperTrade(1m, 100m, 0m, "buy");
        var afterProfit = ExperimentResearchGroupConfiguration.Create(userId, 2, workers, provenance);
        workers[0].UpdateStrategyParameters("""{"changed":true}""");

        var result = await pool.RunConfiguredAsync(userId, afterProfit, new RecordingRunner());

        Assert.Equal(
            beforeProfit.Assignments.Select(x => (x.WorkerId, x.Group, x.RandomSeed)),
            afterProfit.Assignments.Select(x => (x.WorkerId, x.Group, x.RandomSeed)));
        Assert.Single(result.Results);
        Assert.Equal(workers[1].Id, result.Results[0].WorkerId);
        Assert.Single(workers[0].Ledger);
    }

    [Fact]
    public async Task MissingConfigurationSourceIsAnExplicitNoWorkHostDefault()
    {
        var source = new UnconfiguredExperimentResearchGroupConfigurationSource();

        Assert.Null(await source.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static async Task<List<ExperimentWorker>> WorkersAsync(
        InMemoryExperimentWorkerRepository repository,
        Guid userId,
        int count)
    {
        var workers = Enumerable.Range(1, count)
            .Select(index => new ExperimentWorker(
                new Guid(index, 0, 0, new byte[8]),
                userId,
                $"worker-{index}",
                "template-sma",
                "BTCUSDT",
                10_000m,
                Now,
                index))
            .ToList();
        foreach (var worker in workers)
        {
            worker.Start();
            await repository.SaveAsync(worker);
        }

        return workers;
    }

    private static ExperimentResearchProvenance Provenance(bool approved)
    {
        var approval = StrategyApproval.CreateDraft(
            Guid.NewGuid(),
            new StrategyVersion(
                new StrategyTemplateVersionIdentity("template-sma", 1),
                new StrategyParameterSchemaReference("parameters", 1, Fingerprint),
                Fingerprint,
                Now),
            StrategyApprovalActor.Human(Guid.NewGuid()),
            Now,
            new StrategyApprovalRequirements(
                new[] { new ApprovedInstrumentScope(AssetClass.Cryptocurrency, InstrumentId) },
                10,
                1m,
                1m,
                1m,
                TimeSpan.FromHours(1),
                new[] { CandleInterval.OneHour },
                new[] { TradingProductType.Spot },
                new[] { StrategyApprovalMode.Paper },
                new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.OneHour, CandleInterval.OneHour)));
        if (approved)
        {
            var actor = approval.CreatedBy;
            approval = approval.TransitionTo(StrategyApprovalState.UnderReview, actor, Now.AddMinutes(1))
                .TransitionTo(StrategyApprovalState.Approved, actor, Now.AddMinutes(2), actor);
        }

        var dataset = new HistoricalDataset(
            "dataset", "research", "BTCUSDT", "1H",
            Now.AddDays(-2), Now.AddHours(-1), 48, Fingerprint, "v1", Now);
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("research", Fingerprint, Now),
            new StrategyApprovalEvidence(InstrumentId, AssetClass.Cryptocurrency, 100, 10m, 0.1m, 0.01m, Now),
            true, 1m, 1m, true, 1m, 1m, 1m, Fingerprint);
        var gates = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(
            new StrategyRejectionGateEvaluationInput(
                approval,
                approval.Requirements?.TimeframeConfiguration,
                TradingProductType.Spot,
                StrategyApprovalMode.Paper,
                evidence,
                Now));

        return new ExperimentResearchProvenance(
            approval,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("{}"))),
            dataset,
            new ExperimentClassifierReference("platform-regime", 1, Fingerprint),
            evidence.Provenance,
            gates);
    }
}
