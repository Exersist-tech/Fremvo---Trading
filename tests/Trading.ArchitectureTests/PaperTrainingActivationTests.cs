using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingActivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedAuditActions = ["PaperTrainingRequested", "PaperTrainingActivated"];

    [Fact]
    public async Task DefaultActivationSourceStartsNoWorkers()
    {
        var source = new DisabledPaperTrainingActivationSource();

        Assert.Empty(await source.GetActiveOwnerIdsAsync());
    }

    [Fact]
    public async Task RequestIsInertUntilSafetyRoleExplicitlyApprovesAndAuditsIt()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var audit = new InMemoryAuditEventWriter();
        var service = new PaperTrainingActivationService(repository, audit, new FixedTimeProvider());
        var owner = Guid.NewGuid();

        var requested = await service.RequestAsync(owner, owner, RoleType.User, 2, CompletePrerequisites());

        Assert.Equal(PaperTrainingActivationState.Requested, requested.State);
        Assert.Empty(await repository.GetActiveOwnerIdsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ActivateAsync(owner, owner, RoleType.User, "approval-1"));

        var active = await service.ActivateAsync(owner, Guid.NewGuid(), RoleType.Administrator, "approval-1");

        Assert.True(active.IsActive);
        Assert.Equal(new[] { owner }, await repository.GetActiveOwnerIdsAsync());
        Assert.Equal(ExpectedAuditActions, audit.Events.Select(e => e.Action));
        Assert.DoesNotContain(audit.Events, e => (e.After ?? string.Empty).Contains("approval-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncompletePrerequisitesAndInvalidSlotsRefuseWithoutActivation()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(repository, new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RequestAsync(owner, owner, RoleType.User, 1, CompletePrerequisites() with { OutputLedger = false }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.RequestAsync(owner, owner, RoleType.User, ExperimentWorker.MaxWorkersPerUser + 1, CompletePrerequisites()));

        Assert.Empty(await repository.GetActiveOwnerIdsAsync());
    }

    [Fact]
    public async Task SlotsUseOnlyFixedBalancesSeedsAndPlatformCatalog()
    {
        var service = new PaperTrainingActivationService(
            new InMemoryPaperTrainingActivationRepository(), new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();

        var request = await service.RequestAsync(owner, owner, RoleType.User, 10, CompletePrerequisites());

        Assert.Equal(10, request.Slots.Count);
        Assert.All(request.Slots, slot => Assert.Equal(PaperTrainingActivationService.FixedStartingCash, slot.StartingCash));
        Assert.Equal(PaperTrainingActivationService.ApprovedSlots, request.Slots);
        Assert.All(request.Slots, slot => Assert.Equal("experiment-sma-trend", slot.StrategyId));
        Assert.All(request.Slots, slot => Assert.Equal("BTC/USD", slot.Symbol));
    }

    [Fact]
    public async Task OwnerCannotControlAnotherOwnerAndStopsAreImmediate()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var audit = new InMemoryAuditEventWriter();
        var service = new PaperTrainingActivationService(repository, audit, new FixedTimeProvider());
        var owner = Guid.NewGuid();
        await service.RequestAsync(owner, owner, RoleType.User, 1, CompletePrerequisites());
        await service.ActivateAsync(owner, Guid.NewGuid(), RoleType.RiskOfficer, "risk-approval");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DisableAsync(owner, Guid.NewGuid(), RoleType.User));
        var stopped = await service.EmergencyStopAsync(owner, owner, RoleType.User);

        Assert.Equal(PaperTrainingActivationState.EmergencyStopped, stopped.State);
        Assert.Empty(await repository.GetActiveOwnerIdsAsync());
        Assert.Contains(audit.Events, e => e.Action == "PaperTrainingEmergencyStopped");
    }

    private static PaperTrainingPrerequisites CompletePrerequisites() => new(true, true, true, true, true, true);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
