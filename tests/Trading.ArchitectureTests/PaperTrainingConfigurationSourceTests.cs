using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Workers.Experiments;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingConfigurationSourceTests
{
    [Fact]
    public async Task ScannerActivationCreatesNoWaitingWorker()
    {
        var ownerId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 21, 19, 18, 7, TimeSpan.Zero);
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [],
                new(true, true, true, true, true, true),
                now,
                ownerId),
            null);
        var workers = new InMemoryExperimentWorkerRepository();
        var source = new PaperTrainingConfigurationSource(
            workers,
            new FixedTimeProvider(now),
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations);

        Assert.Null(await source.GetAsync(ownerId, CancellationToken.None));
        Assert.Empty(await workers.ListAsync(ownerId));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ScannerAdmissionPreservesPinnedStrategyVersionAcrossReads(int strategyVersion)
    {
        var ownerId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 21, 19, 18, 7, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "XBT/EUR",
            Interval = CandleInterval.FifteenMinutes,
            ProvenanceId = "scan-1234567890ABCDEF12345678",
            StrategyVersion = strategyVersion
        };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                now,
                ownerId),
            null);
        var workers = new InMemoryExperimentWorkerRepository();
        var source = new PaperTrainingConfigurationSource(
            workers,
            new FixedTimeProvider(now),
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations);

        var configuration = await source.GetAsync(ownerId, CancellationToken.None);
        Assert.NotNull(configuration);
        var worker = Assert.Single(await workers.ListAsync(ownerId));
        Assert.Equal("Paper opportunity scan-1234567890ABCDEF12345678", worker.Name);
        Assert.Equal(strategyVersion, Assert.Single(configuration.Assignments)
            .Provenance.Approval.StrategyVersion.Identity.Version);

        var changed = (await activations.GetAsync(ownerId))! with { ChangedAtUtc = now.AddMinutes(5) };
        Assert.True(await activations.TrySaveAsync(changed, PaperTrainingActivationState.Active));
        configuration = await source.GetAsync(ownerId, CancellationToken.None);
        Assert.Equal(strategyVersion, Assert.Single(configuration!.Assignments)
            .Provenance.Approval.StrategyVersion.Identity.Version);
        Assert.Single(await workers.ListAsync(ownerId));
    }

    [Fact]
    public async Task CreatesConfigurationAlignedToTheSelectedInterval()
    {
        var ownerId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 21, 19, 18, 7, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "XBT/EUR",
            Interval = CandleInterval.FifteenMinutes
        };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                now,
                ownerId),
            null);
        var workers = new InMemoryExperimentWorkerRepository();
        var source = new PaperTrainingConfigurationSource(
            workers,
            new FixedTimeProvider(now),
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations);

        var configuration = await source.GetAsync(ownerId, CancellationToken.None);

        Assert.NotNull(configuration);
        var assignment = Assert.Single(configuration.Assignments);
        Assert.Equal(
            CandleInterval.FifteenMinutes,
            assignment.Provenance.Approval.Requirements!.TimeframeConfiguration!.Signal);
        Assert.Equal("15M", assignment.Provenance.Dataset.Interval);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 21, 19, 15, 0, TimeSpan.Zero),
            assignment.Provenance.Dataset.ToUtc);
        Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, assignment.Provenance.Dataset.CandleCount);
        Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, assignment.Provenance.Approval.Requirements.MinimumClosedHistoryCandles);
        Assert.True(assignment.Provenance.GateEvaluation.Accepted);
        Assert.Equal(ExperimentWorkerStatus.Running, Assert.Single(await workers.ListAsync(ownerId)).Status);
    }

    [Fact]
    public async Task ExactCloseBoundaryWaitsForTheNextTickBeforeCreatingDatasetProvenance()
    {
        var owner = Guid.NewGuid();
        var boundary = new DateTimeOffset(2026, 9, 21, 19, 15, 0, TimeSpan.Zero);
        var clock = new AdvancingTimeProvider(boundary);
        var activations = new InMemoryPaperTrainingActivationRepository();
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Interval = CandleInterval.FifteenMinutes,
            ProvenanceId = "scan-1234567890ABCDEF12345678"
        };
        Assert.True(await activations.TrySaveAsync(new(
            owner, PaperTrainingActivationState.Active, [slot],
            new(true, true, true, true, true, true), boundary, owner), null));
        var workers = new InMemoryExperimentWorkerRepository();
        var source = new PaperTrainingConfigurationSource(workers, clock,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations);

        Assert.Null(await source.GetAsync(owner, CancellationToken.None));
        Assert.Empty(await workers.ListAsync(owner));
        clock.UtcNow = boundary.AddSeconds(1);
        Assert.NotNull(await source.GetAsync(owner, CancellationToken.None));
        Assert.Single(await workers.ListAsync(owner));
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
