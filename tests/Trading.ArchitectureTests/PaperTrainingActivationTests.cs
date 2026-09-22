using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Market;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingActivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedAuditActions = ["PaperTrainingStarted"];
    private static readonly int[] ExpectedGroupSizes = [4, 3, 3];
    private static readonly string[] ExpectedCatalogSymbols =
        ["XRP/EUR", "TRX/EUR", "DOGE/EUR", "ADA/EUR", "XRP/EUR", "TRX/EUR", "BTC/USD", "BTC/USD", "DOGE/EUR", "ADA/EUR"];

    [Fact]
    public async Task DefaultActivationSourceStartsNoWorkers()
    {
        var source = new DisabledPaperTrainingActivationSource();

        Assert.Empty(await source.GetActiveOwnerIdsAsync());
    }

    [Fact]
    public async Task OwnerStartsImmediatelyWhenAllPaperPrerequisitesPassesAndAuditsIt()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var audit = new InMemoryAuditEventWriter();
        var service = new PaperTrainingActivationService(repository, audit, new FixedTimeProvider());
        var owner = Guid.NewGuid();

        var active = await service.StartAsync(owner, owner, RoleType.User, 2, CompletePrerequisites());

        Assert.True(active.IsActive);
        Assert.Equal(new[] { owner }, await repository.GetActiveOwnerIdsAsync());
        Assert.Equal(ExpectedAuditActions, audit.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task IncompletePrerequisitesAndInvalidSlotsRefuseWithoutActivation()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(repository, new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartAsync(owner, owner, RoleType.User, 1, CompletePrerequisites() with { OutputLedger = false }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.StartAsync(owner, owner, RoleType.User, ExperimentWorker.MaxWorkersPerUser + 1, CompletePrerequisites()));

        Assert.Empty(await repository.GetActiveOwnerIdsAsync());
    }

    [Fact]
    public async Task SlotsUseOnlyFixedBalancesSeedsAndPlatformCatalog()
    {
        var service = new PaperTrainingActivationService(
            new InMemoryPaperTrainingActivationRepository(), new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();

        var request = await service.StartAsync(owner, owner, RoleType.User, 10, CompletePrerequisites());

        Assert.Equal(10, request.Slots.Count);
        Assert.All(request.Slots, slot => Assert.Equal(PaperTrainingActivationService.FixedStartingCash, slot.StartingCash));
        Assert.Equal(PaperTrainingActivationService.ApprovedSlots.Take(10), request.Slots);
        Assert.Equal(15, PaperTrainingActivationService.ApprovedSlots.Count);
        Assert.Equal(10, request.Slots.Select(slot => slot.StrategyId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ExpectedGroupSizes, request.Slots.GroupBy(slot => slot.Group).OrderBy(group => group.Key).Select(group => group.Count()));
        Assert.All(request.Slots, slot => Assert.StartsWith("phase5b-", slot.ProvenanceId, StringComparison.Ordinal));
        Assert.Equal(ExpectedCatalogSymbols, request.Slots.Select(slot => slot.Symbol));
    }

    [Fact]
    public async Task OwnerCannotControlAnotherOwnerAndStopsAreImmediate()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var audit = new InMemoryAuditEventWriter();
        var service = new PaperTrainingActivationService(repository, audit, new FixedTimeProvider());
        var owner = Guid.NewGuid();
        await service.StartAsync(owner, owner, RoleType.User, 1, CompletePrerequisites());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DisableAsync(owner, Guid.NewGuid(), RoleType.User));
        var stopped = await service.EmergencyStopAsync(owner, owner, RoleType.User);

        Assert.Equal(PaperTrainingActivationState.EmergencyStopped, stopped.State);
        Assert.Empty(await repository.GetActiveOwnerIdsAsync());
        Assert.Contains(audit.Events, e => e.Action == "PaperTrainingEmergencyStopped");
    }

    [Fact]
    public async Task AdministratorOrRiskOfficerMayStartForAnotherOwnerButOrdinaryUserMayNot()
    {
        var service = new PaperTrainingActivationService(
            new InMemoryPaperTrainingActivationRepository(), new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.StartAsync(owner, Guid.NewGuid(), RoleType.User, 1, CompletePrerequisites()));
        Assert.True((await service.StartAsync(owner, Guid.NewGuid(), RoleType.Administrator, 1, CompletePrerequisites())).IsActive);
    }

    [Fact]
    public async Task QualifiedStartActivatesQualifiedAndExplicitPaperExplorationSlotsAndRetainsEvidence()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            repository, new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();
        var selected = PaperTrainingActivationService.ApprovedSlots.Take(2).ToArray();
        var results = new[]
        {
            new PaperTrainingQualificationResult(
                selected[0].Slot, selected[0].Symbol, true, 2m, 4, 5m, new string('A', 64), "Passed."),
            new PaperTrainingQualificationResult(
                selected[1].Slot, selected[1].Symbol, false, -1m, 2, 8m, new string('B', 64), "Failed.",
                PaperOnlyExploration: true)
        };

        var activation = await service.StartQualifiedAsync(
            owner, owner, RoleType.User, selected, results, CompletePrerequisites());

        Assert.True(activation.IsActive);
        Assert.Equal(selected, activation.Slots);
        Assert.Equal(results, activation.QualificationResults);
        Assert.Equal(new[] { owner }, await repository.GetActiveOwnerIdsAsync());
    }

    [Fact]
    public async Task QualifiedStartDoesNotActivateARejectedNonExplorationSlot()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            repository, new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();
        var selected = PaperTrainingActivationService.ApprovedSlots.Take(2).ToArray();
        var results = selected.Select(slot => new PaperTrainingQualificationResult(
            slot.Slot, slot.Symbol, false, -1m, 2, 8m, new string('B', 64), "Failed.")).ToArray();

        var activation = await service.StartQualifiedAsync(
            owner, owner, RoleType.User, selected, results, CompletePrerequisites());

        Assert.Equal(PaperTrainingActivationState.Disabled, activation.State);
        Assert.Empty(activation.Slots);
    }

    [Fact]
    public async Task QualifiedStartRejectsEvidenceForAnotherInterval()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            repository, new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();
        var selected = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Interval = CandleInterval.FiveMinutes
        };
        var mismatched = new PaperTrainingQualificationResult(
            selected.Slot,
            selected.Symbol,
            true,
            1m,
            3,
            2m,
            new string('C', 64),
            "Passed.",
            selected.StrategyId,
            Interval: CandleInterval.OneHour);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.StartQualifiedAsync(
                owner, owner, RoleType.User, [selected], [mismatched], CompletePrerequisites()));
    }

    [Fact]
    public async Task QualifiedStartAllowsAnApprovedTemplateOnADiscoveredSymbol()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            repository, new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();
        var discovered = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "ETH/USD",
            Seed = 42
        };
        var result = new PaperTrainingQualificationResult(
            discovered.Slot, discovered.Symbol, true, 1m, 3, 2m, new string('C', 64), "Passed.");

        var activation = await service.StartQualifiedAsync(
            owner, owner, RoleType.User, [discovered], [result], CompletePrerequisites());

        Assert.Equal("ETH/USD", Assert.Single(activation.Slots).Symbol);
        Assert.Equal(
            PaperTrainingAutoSelectionService.ApprovedIntervals,
            (await repository.GetActiveSubscriptionsAsync())
                .Where(subscription => subscription.Symbol == "ETH/USD")
                .Select(subscription => subscription.Interval));
    }

    [Fact]
    public void QualificationPolicyAllowsOnlyStricterUserGates()
    {
        Assert.Equal(
            PaperTrainingQualificationGate.PlatformDefault,
            PaperTrainingQualificationGate.PlatformDefault.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PaperTrainingQualificationGate(-0.01m, 3, 20m).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PaperTrainingQualificationGate(0m, 2, 20m).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PaperTrainingQualificationGate(0m, 3, 20.01m).Validate());
    }

    private static PaperTrainingPrerequisites CompletePrerequisites() => new(true, true, true, true, true, true);

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
