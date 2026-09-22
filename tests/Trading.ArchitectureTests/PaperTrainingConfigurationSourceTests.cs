using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Workers.Experiments;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingConfigurationSourceTests
{
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
        Assert.Equal(35, assignment.Provenance.Dataset.CandleCount);
        Assert.Equal(35, assignment.Provenance.Approval.Requirements.MinimumClosedHistoryCandles);
        Assert.True(assignment.Provenance.GateEvaluation.Accepted);
        Assert.Equal(ExperimentWorkerStatus.Running, Assert.Single(await workers.ListAsync(ownerId)).Status);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
