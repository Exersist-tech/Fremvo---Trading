using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.ArchitectureTests;

public sealed class ContinuousPaperOpportunityScannerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ScannerReservesNoMoreThanTheCurrentPaperPlanAllows(int maximum)
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        await new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), new FixedTimeProvider(now))
            .StartScannerAsync(owner, owner, RoleType.User,
                new(true, true, true, true, true, true));
        var history = new RisingHistorySource(now, 8);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(history, history, PaperTrainingUniversePolicy.PlatformDefault),
            history, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations, new InMemoryExperimentWorkerRepository(),
            new ApprovingStager(), new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger(), new FixedPaperLimit(maximum));

        var scan = await scanner.ScanAsync(owner);

        Assert.True(scan.Persisted);
        Assert.True(scan.QualifiedCandidates > 0);
        Assert.Equal(maximum, scan.AdmittedCandidates);
        var persisted = (await activations.GetAsync(owner))!;
        Assert.Equal(maximum, persisted.Slots.Count);
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<PaperTrainingQualificationResult>>(
            persisted.Qualifications), result =>
            result.CandidateDisposition == PaperTrainingCandidateDisposition.Queued
            && result.Reason.Contains(maximum == 0
                    ? "No new paper-worker capacity"
                    : "All 1 allowed paper-worker slots are reserved",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpiredPaperLimitKeepsPreviouslyReservedSlotsWithoutAdmittingNewOnes()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var active = await new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), new FixedTimeProvider(now))
            .StartScannerAsync(owner, owner, RoleType.User,
                new(true, true, true, true, true, true));
        var reserved = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            ProvenanceId = "scan-1234567890ABCDEF12345678"
        };
        Assert.True(await activations.TrySaveAsync(active with { Slots = [reserved] }, active.State));
        var history = new RisingHistorySource(now, 8);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(history, history, PaperTrainingUniversePolicy.PlatformDefault),
            history, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations,
            new InMemoryExperimentWorkerRepository(), new ApprovingStager(), new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger(), new FixedPaperLimit(0));

        var scan = await scanner.ScanAsync(owner);

        Assert.True(scan.Persisted);
        Assert.Equal(0, scan.AdmittedCandidates);
        var persisted = (await activations.GetAsync(owner))!;
        Assert.Equal(reserved.ProvenanceId, Assert.Single(persisted.Slots).ProvenanceId);
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<PaperTrainingQualificationResult>>(
            persisted.Qualifications), result =>
            result.CandidateDisposition == PaperTrainingCandidateDisposition.Queued
            && result.Reason.Contains("No new paper-worker capacity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TrialExpiringDuringScanDiscardsOnlyNewReservationsBeforePersistence()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        await new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), new FixedTimeProvider(now))
            .StartScannerAsync(owner, owner, RoleType.User,
                new(true, true, true, true, true, true));
        var history = new RisingHistorySource(now, 8);
        var limit = new ExpiringPaperLimit();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(history, history, PaperTrainingUniversePolicy.PlatformDefault),
            history, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations,
            new InMemoryExperimentWorkerRepository(), new ApprovingStager(), new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger(), limit);

        var scan = await scanner.ScanAsync(owner);

        Assert.True(scan.Persisted);
        Assert.True(scan.QualifiedCandidates > 0);
        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.True(limit.Reads >= 3);
    }

    private sealed class ExpiringPaperLimit : IPaperWorkerAdmissionLimit
    {
        public int Reads { get; private set; }
        public Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(++Reads <= 2 ? 1 : 0);
    }

    private sealed class FixedPaperLimit(int maximum) : IPaperWorkerAdmissionLimit
    {
        public Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(maximum);
    }

    [Fact]
    public async Task CompletedScannerPersistsAnObservedUniverseIndependentOfBoundedObservationsAsync()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var context = new TradingDbContext(options))
        {
            var activations = new EfPaperTrainingActivationRepository(context);
            await new PaperTrainingActivationService(activations,
                new InMemoryAuditEventWriter(), new FixedTimeProvider(now))
                .StartScannerAsync(owner, owner, RoleType.User,
                    new(true, true, true, true, true, true));
            var source = new RisingHistorySource(now, 2);
            var scanner = new ContinuousPaperOpportunityScanner(
                new PaperTrainingUniverseDiscovery(source, source,
                    PaperTrainingUniversePolicy.PlatformDefault),
                source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
                activations, new InMemoryExperimentWorkerRepository(),
                new ApprovingStager(), new FixedTimeProvider(now),
                new InMemoryExperimentPaperExecutionLedger());

            var result = await scanner.ScanAsync(owner);
            Assert.True(result.Persisted);
            Assert.Equal(2, result.EligiblePairs);
            Assert.False((await scanner.ScanAsync(owner)).Persisted);
            Assert.Single(context.AuditEvents.Where(item =>
                item.Action == "PaperScanner.UniverseObserved"));
        }

        await using var reopened = new TradingDbContext(options);
        var saved = await new EfPaperScanUniverseSnapshotRepository(reopened)
            .GetAsync(owner, now);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Members.Count);
        Assert.Equal(2, saved.DailyEvidence.Count);
        Assert.Equal(PaperScanUniverseSnapshot.ExpandedSchemaVersion, saved.EvidenceSchemaVersion);
        Assert.NotEmpty(saved.SeriesEvidence!);
        Assert.Equal(11, saved.StrategyEvidence!.Count);
        Assert.All(saved.StrategyEvidence, item => Assert.True(item.ParametersValid));
        Assert.Equal(now, saved.ObservedAtUtc);
        Assert.Equal(PaperTrainingUniversePolicy.PlatformDefault.MaximumCandidatePairs,
            saved.Policy.MaximumCandidatePairs);
        Assert.Null((await new EfPaperTrainingActivationRepository(reopened)
            .GetAsync(owner))!.PendingUniverseSnapshot);
    }

    [Fact]
    public async Task ScanQueuesCandidatesAndReservesAtMostTenWorkersOnlyAfterConsensus()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activationRepository = new InMemoryPaperTrainingActivationRepository();
        var activationService = new PaperTrainingActivationService(
            activationRepository,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await activationService.StartScannerAsync(
            owner,
            owner,
            RoleType.User,
            new(true, true, true, true, true, true));
        var workers = new InMemoryExperimentWorkerRepository();
        var source = new RisingHistorySource(now, 8);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(
                source,
                source,
                PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activationRepository,
            workers,
            new ApprovingStager(),
            new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger());

        var result = await scanner.ScanAsync(owner);
        var activation = await activationRepository.GetAsync(owner);

        Assert.True(result.Persisted);
        Assert.True(result.QualifiedCandidates > 0);
        var metrics = Assert.Single(activation!.QualificationResults
            .Where(observation => observation.StrategyId == "platform.scanner")).ScanMetrics;
        Assert.NotNull(metrics);
        Assert.Equal(result.EligiblePairs, metrics.EligiblePairs);
        Assert.Equal(result.AdmittedCandidates, metrics.AdmittedCandidates);
        var snapshot = Assert.IsType<PaperScanUniverseSnapshot>(activation.PendingUniverseSnapshot);
        snapshot.Validate();
        Assert.Equal(owner, snapshot.OwnerId);
        Assert.Equal(result.SignalBoundaryUtc, snapshot.SignalBoundaryUtc);
        Assert.Equal(result.EligiblePairs, snapshot.Members.Count);
        Assert.Equal(result.EligiblePairs, snapshot.DailyEvidence.Count);
        Assert.Contains(snapshot.DailyEvidence, item => item.CandleFingerprint is not null);
        Assert.Equal(PaperScanUniverseSnapshot.ExpandedSchemaVersion, snapshot.EvidenceSchemaVersion);
        Assert.NotEmpty(snapshot.SeriesEvidence!);
        Assert.Equal(11, snapshot.StrategyEvidence!.Count);
        Assert.Contains(snapshot.StrategyEvidence, item =>
            item.FamilyId == "platform.three-swing-channel-divergence" && !item.Evaluated);
        Assert.All(snapshot.StrategyEvidence.Where(item => item.Evaluated),
            item => Assert.True(item.ParametersValid));
        Assert.All(snapshot.Members, member => Assert.True(member.PairFilters.MinimumNotional > 0m));
        Assert.InRange(result.AdmittedCandidates, 1, ExperimentWorker.MaxWorkersPerUser);
        Assert.Equal(result.AdmittedCandidates, activation.Slots.Count);
        Assert.InRange(
            activation.Slots.Select(slot => slot.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            1,
            8);
        Assert.All(activation.Slots, slot =>
        {
            Assert.Equal(slot.StrategyId is "platform.donchian-breakout-ensemble"
                or "platform.bollinger-mean-reversion"
                or "platform.rsi-pullback" or "platform.macd-volume"
                or "platform.ema-trend-continuation"
                or "platform.volatility-compression-breakout" ? 5
                : slot.StrategyId is "platform.cross-sectional-momentum-rotation"
                    or "platform.three-swing-channel-divergence" ? 4
                : slot.StrategyId == "platform.regime-switching-ensemble" ? 5
                : 3, slot.StrategyVersion);
            Assert.Equal(
                slot.StrategyId,
                activation.ConfiguredStrategies.Single(assignment => assignment.Slot == slot.Slot).StrategyId);
        });
        Assert.Empty(await workers.ListAsync(owner));
        Assert.All(activation.Slots, slot => Assert.StartsWith("scan-", slot.ProvenanceId, StringComparison.Ordinal));
        Assert.Contains(activation.QualificationResults, result =>
            result.StrategyId != "platform.scanner"
            && activation.ConfiguredStrategies.Any(assignment => assignment.StrategyId == result.StrategyId));
        var admittedObservations = activation.QualificationResults
            .Where(result => result.CandidateDisposition == PaperTrainingCandidateDisposition.Admitted)
            .ToArray();
        Assert.Equal(result.AdmittedCandidates, admittedObservations.Length);
        Assert.All(admittedObservations, observation =>
        {
            Assert.True(observation.Accepted);
            Assert.True(observation.BullishChecks >= observation.RequiredBullishChecks);
            Assert.NotNull(observation.SignalCloseUtc);
        });
    }

    [Fact]
    public async Task SameFiveMinuteBoundaryIsIdempotent()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var source = new RisingHistorySource(now, 2);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            new InMemoryExperimentWorkerRepository(),
            new ApprovingStager(),
            new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger());

        var first = await scanner.ScanAsync(owner);
        var second = await scanner.ScanAsync(owner);

        Assert.True(first.EvaluatedCandidates > 0);
        Assert.True(first.Persisted);
        Assert.False(second.Persisted);
        Assert.Equal(0, second.EvaluatedCandidates);
        Assert.Equal(0, second.AdmittedCandidates);
    }

    [Fact]
    public async Task UnstagedCandidateIsRejectedWithoutReservingAWorker()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        await new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), new FixedTimeProvider(now))
            .StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var source = new RisingHistorySource(now, 2);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations, new InMemoryExperimentWorkerRepository(),
            new RejectingStager(), new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger());

        var result = await scanner.ScanAsync(owner);
        var activation = await activations.GetAsync(owner);

        Assert.True(result.QualifiedCandidates > 0);
        Assert.Equal(0, result.AdmittedCandidates);
        Assert.Empty(activation!.Slots);
        Assert.Contains(activation.QualificationResults, observation =>
            observation.CandidateDisposition == PaperTrainingCandidateDisposition.Rejected
            && observation.Reason.Contains("durable worker candle store", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncompleteBearishSetupDoesNotReserveAFlatWorker()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var source = new RisingHistorySource(now, 2, rising: false);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            new InMemoryExperimentWorkerRepository(),
            new ApprovingStager(),
            new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger());

        await scanner.ScanAsync(owner);
        var activation = await activations.GetAsync(owner);

        Assert.NotNull(activation);
        Assert.Empty(activation.Slots);
        Assert.Contains(activation.QualificationResults, result =>
            !result.Accepted
            && result.StrategyId == "platform.ema-trend-continuation"
            && result.Reason.Contains("Missing bearish entry checks: bounded-pullback", StringComparison.Ordinal)
            && result.CandidateDisposition == PaperTrainingCandidateDisposition.Hold
            && result.BullishChecks < result.RequiredBullishChecks
            && result.SignalCloseUtc is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedScannerWorkerReleasesCapacityOnlyAfterExecutionIsResolved(bool unresolved)
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var clock = new FixedTimeProvider(now);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            clock);
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var workers = new InMemoryExperimentWorkerRepository();
        var executions = new InMemoryExperimentPaperExecutionLedger();
        var source = new RisingHistorySource(now, 1);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            workers,
            new ApprovingStager(),
            clock,
            executions);
        await scanner.ScanAsync(owner);
        var admitted = (await activations.GetAsync(owner))!.Slots;
        Assert.NotEmpty(admitted);
        foreach (var slot in admitted)
        {
            var worker = new ExperimentWorker(
                Guid.NewGuid(),
                owner,
                ContinuousPaperOpportunityScanner.WorkerName(slot),
                slot.StrategyId,
                slot.Symbol,
                slot.StartingCash,
                now,
                slot.Seed);
            worker.Start();
            worker.Complete();
            await workers.SaveAsync(worker);
            if (unresolved)
            {
                var key = new ExperimentDecisionKey(owner, worker.Id, 1, ExperimentResearchGroup.A,
                    slot.StrategyId, 1, new string('A', 64), slot.Symbol, CandleInterval.OneHour,
                    now.AddHours(-1), now, now);
                await executions.ClaimAsync(owner, new(key, "pending", ExperimentPaperExecutionStatus.Claimed));
            }
        }

        clock.UtcNow = now.AddMinutes(5);
        await scanner.ScanAsync(owner);

        if (unresolved)
            Assert.Equal(admitted.Count, (await activations.GetAsync(owner))!.Slots.Count);
        else
            Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Equal(admitted.Count, (await workers.ListAsync(owner)).Count);
    }

    [Fact]
    public async Task ReleasedCapacityAdmitsFreshReplacementOpportunities()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-2);
        var clock = new FixedTimeProvider(now);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            clock);
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var workers = new InMemoryExperimentWorkerRepository();
        ContinuousPaperOpportunityScanner CreateScanner(DateTimeOffset sourceNow)
        {
            var source = new RisingHistorySource(sourceNow, 2);
            return new ContinuousPaperOpportunityScanner(
                new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
                source,
                ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
                activations,
                workers,
                new ApprovingStager(),
                clock,
                new InMemoryExperimentPaperExecutionLedger());
        }

        await CreateScanner(now).ScanAsync(owner);
        var firstAdmissions = (await activations.GetAsync(owner))!.Slots;
        Assert.NotEmpty(firstAdmissions);
        foreach (var slot in firstAdmissions)
        {
            var worker = new ExperimentWorker(
                Guid.NewGuid(),
                owner,
                ContinuousPaperOpportunityScanner.WorkerName(slot),
                slot.StrategyId,
                slot.Symbol,
                slot.StartingCash,
                now,
                slot.Seed);
            worker.Start();
            worker.Complete();
            await workers.SaveAsync(worker);
        }

        clock.UtcNow = now.AddHours(1);
        var result = await CreateScanner(clock.UtcNow).ScanAsync(owner);
        var replacementAdmissions = (await activations.GetAsync(owner))!.Slots;

        Assert.True(result.AdmittedCandidates > 0);
        Assert.NotEmpty(replacementAdmissions);
        Assert.All(replacementAdmissions, slot => Assert.DoesNotContain(firstAdmissions, previous =>
            previous.ProvenanceId.Equals(slot.ProvenanceId, StringComparison.Ordinal)));
        Assert.Equal(firstAdmissions.Count, (await workers.ListAsync(owner)).Count);
    }

    [Fact]
    public async Task ConcurrentScansNeverReserveMoreThanTenAdmissions()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var source = new RisingHistorySource(now, 12);
        ContinuousPaperOpportunityScanner CreateScanner() => new(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            new InMemoryExperimentWorkerRepository(),
            new ApprovingStager(),
            new FixedTimeProvider(now),
            new InMemoryExperimentPaperExecutionLedger());

        await Task.WhenAll(CreateScanner().ScanAsync(owner), CreateScanner().ScanAsync(owner));

        var activation = await activations.GetAsync(owner);
        Assert.NotNull(activation);
        Assert.InRange(activation.Slots.Count, 1, ExperimentWorker.MaxWorkersPerUser);
        Assert.Equal(activation.Slots.Count, activation.Slots.Select(slot => slot.Slot).Distinct().Count());
    }

    [Fact]
    public async Task RegimeAdmissionStagesAndPersistsOnePinnedComponentWithItsUniverse()
    {
        var now = AlignDown(DateTimeOffset.UtcNow.AddDays(-1), TimeSpan.FromDays(1)).AddHours(12);
        var owner = Guid.NewGuid();
        var activationRepository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activationRepository, new InMemoryAuditEventWriter(), new FixedTimeProvider(now));
        var prerequisites = new PaperTrainingPrerequisites(true, true, true, true, true, true);
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        await service.DisableAsync(owner, owner, RoleType.User);
        await service.ConfigureScannerStrategiesAsync(owner, owner, RoleType.User,
            Enumerable.Range(1, 10).Select(index =>
                new PaperTrainingStrategyAssignment(index, "platform.regime-switching-ensemble")).ToArray());
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        var source = new RegimeHistorySource(now);
        var stager = new RecordingStager();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activationRepository,
            new InMemoryExperimentWorkerRepository(), stager,
            new FixedTimeProvider(now), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);
        var activation = await activationRepository.GetAsync(owner);
        Assert.True(scan.Persisted);
        Assert.NotEmpty(activation!.Slots);
        Assert.All(activation.Slots, slot =>
        {
            Assert.Equal(5, slot.StrategyVersion);
            var selected = Assert.IsType<PaperRegimeComponentSelection>(slot.SelectedComponent);
            Assert.Equal("platform.ema-trend-continuation", selected.FamilyId);
            Assert.Equal(["ETH/EUR", "XBT/EUR"], selected.UniverseSymbols);
            Assert.Equal(4, selected.Version);
            Assert.Equal(CandleInterval.FourHours, selected.SignalInterval);
            var approved = ApprovedExperimentStrategyRegistry.CreatePlatformDefault()
                .ResolveDefinition(selected.FamilyId, selected.Version);
            Assert.NotEmpty(approved.ContentFingerprint);
            Assert.True(stager.Staged.TryGetValue(slot.Symbol, out var intervals));
            Assert.All(ApprovedConsensusStrategyProfiles.RequiredIntervals(
                    selected.FamilyId, selected.SignalInterval),
                interval => Assert.Contains(interval, intervals!));
        });
    }

    [Fact]
    public async Task RegimeAdmissionBlocksWhenAnyRankingUniverseMemberCannotBeStaged()
    {
        var now = AlignDown(DateTimeOffset.UtcNow.AddDays(-1), TimeSpan.FromDays(1)).AddHours(12);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), new FixedTimeProvider(now));
        var prerequisites = new PaperTrainingPrerequisites(true, true, true, true, true, true);
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        await service.DisableAsync(owner, owner, RoleType.User);
        await service.ConfigureScannerStrategiesAsync(owner, owner, RoleType.User,
            Enumerable.Range(1, 10).Select(index =>
                new PaperTrainingStrategyAssignment(index, "platform.regime-switching-ensemble")).ToArray());
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        var source = new RegimeHistorySource(now, weakBenchmark: true);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations,
            new InMemoryExperimentWorkerRepository(), new MissingUniverseStager(),
            new FixedTimeProvider(now), new InMemoryExperimentPaperExecutionLedger());

        var result = await scanner.ScanAsync(owner);
        var activation = await activations.GetAsync(owner);
        Assert.True(result.QualifiedCandidates > 0);
        Assert.Equal(0, result.AdmittedCandidates);
        Assert.Empty(activation!.Slots);
        Assert.Contains(activation.QualificationResults, observation =>
            observation.StrategyId == "platform.regime-switching-ensemble"
            && observation.Reason.Contains("complete ranked-universe daily snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevisedRelativeStrengthIsEvaluatedOnlyOnHoursFollowingTheFourHourSetup()
    {
        var setupClose = AlignDown(DateTimeOffset.UtcNow.AddDays(-1), TimeSpan.FromDays(1))
            .AddHours(12);
        var clock = new FixedTimeProvider(setupClose);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), clock);
        var prerequisites = new PaperTrainingPrerequisites(true, true, true, true, true, true);
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        await service.DisableAsync(owner, owner, RoleType.User);
        await service.ConfigureScannerStrategiesAsync(owner, owner, RoleType.User,
            Enumerable.Range(1, 10).Select(index =>
                new PaperTrainingStrategyAssignment(index, "platform.relative-strength-pullback-rotation"))
                .ToArray());
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        var workers = new InMemoryExperimentWorkerRepository();
        async Task<ContinuousPaperScanResult> ScanAsync(DateTimeOffset boundary)
        {
            clock.UtcNow = boundary;
            var source = new RegimeHistorySource(boundary);
            return await new ContinuousPaperOpportunityScanner(
                new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
                source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations,
                workers, new ApprovingStager(), clock,
                new InMemoryExperimentPaperExecutionLedger()).ScanAsync(owner);
        }

        var premature = await ScanAsync(setupClose);
        var later = await ScanAsync(setupClose.AddHours(1));
        var activation = await activations.GetAsync(owner);

        Assert.Equal(0, premature.EvaluatedCandidates);
        Assert.True(later.EvaluatedCandidates > 0);
        Assert.Contains(activation!.QualificationResults, observation =>
            observation.StrategyId == "platform.relative-strength-pullback-rotation"
            && observation.SignalCloseUtc == setupClose.AddHours(1));
    }

    [Fact]
    public async Task LegacyRelativeProtectionSettingsBlockNewAdmissionsEvenAfterRankingReview()
    {
        const string family = "platform.relative-strength-pullback-rotation";
        var boundary = AlignDown(DateTimeOffset.UtcNow.AddDays(-1), TimeSpan.FromDays(1))
            .AddHours(13);
        var owner = Guid.NewGuid();
        var clock = new FixedTimeProvider(boundary);
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), clock);
        var prerequisites = new PaperTrainingPrerequisites(true, true, true, true, true, true);
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        await service.DisableAsync(owner, owner, RoleType.User);
        await service.ConfigureScannerStrategiesAsync(owner, owner, RoleType.User,
            Enumerable.Range(1, 10).Select(index => new PaperTrainingStrategyAssignment(
                index, family, """{"relativeRankingModel":"dailyExcessBreadth"}""")).ToArray());
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        var source = new RegimeHistorySource(boundary);
        var scan = await new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations,
            new InMemoryExperimentWorkerRepository(), new ApprovingStager(), clock,
            new InMemoryExperimentPaperExecutionLedger()).ScanAsync(owner);

        Assert.True(scan.EvaluatedCandidates > 0);
        Assert.Equal(0, scan.AdmittedCandidates);
        var activation = (await activations.GetAsync(owner))!;
        Assert.Empty(activation.Slots);
        Assert.Contains(activation.QualificationResults, observation =>
            observation.StrategyId == family
            && observation.Reason.Contains("protection settings need owner review", StringComparison.Ordinal));
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - value.UtcTicks % interval.Ticks, TimeSpan.Zero);

    private sealed class ApprovingStager : IPaperScanEvidenceStager
    {
        public Task<bool> StageAsync(
            string symbol,
            DateTimeOffset boundaryUtc,
            IReadOnlyCollection<ExperimentCandleSeries> series,
            CancellationToken cancellationToken = default)
        {
            Assert.NotEmpty(series);
            Assert.All(series, window => Assert.Equal(symbol, window.Symbol));
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingStager : IPaperScanEvidenceStager
    {
        public Dictionary<string, CandleInterval[]> Staged { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> StageAsync(string symbol, DateTimeOffset boundaryUtc,
            IReadOnlyCollection<ExperimentCandleSeries> series, CancellationToken cancellationToken = default)
        {
            Staged[symbol] = Staged.GetValueOrDefault(symbol, [])
                .Concat(series.Select(item => item.Interval)).Distinct().ToArray();
            return Task.FromResult(true);
        }
    }

    private sealed class MissingUniverseStager : IPaperScanEvidenceStager
    {
        public Task<bool> StageAsync(string symbol, DateTimeOffset boundaryUtc,
            IReadOnlyCollection<ExperimentCandleSeries> series, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> StageUniverseDailyAsync(string symbol, DateTimeOffset boundaryUtc,
            IReadOnlyCollection<ExperimentCandleSeries> series, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class RejectingStager : IPaperScanEvidenceStager
    {
        public Task<bool> StageAsync(
            string symbol,
            DateTimeOffset boundaryUtc,
            IReadOnlyCollection<ExperimentCandleSeries> series,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    internal sealed class RegimeHistorySource(DateTimeOffset now, bool weakBenchmark = false)
        : ITradablePairSource, IHistoricalCandleSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>(
            [
                new TradablePair("ETHEUR", "ETH/EUR", "ETH", "EUR", true, .0001m, .0001m, .0001m, 10m),
                new TradablePair("XBTEUR", "XBT/EUR", "XBT", "EUR", true, .0001m, .0001m, .0001m, 10m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            var duration = TimeSpan.FromMinutes((int)interval);
            var close = AlignDown(now, duration);
            var isWeakBenchmark = weakBenchmark && symbol == "XBT/EUR";
            var slope = isWeakBenchmark ? -.04m : symbol == "XBT/EUR" ? .04m : .05m;
            var series = Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                .Select(index =>
                {
                    var open = close.AddTicks(duration.Ticks *
                        (index - ApprovedConsensusStrategyProfiles.RequiredHistory));
                    var price = (isWeakBenchmark ? 500m : 100m) + index * slope
                        + (interval == CandleInterval.OneDay && !isWeakBenchmark
                            && index >= ApprovedConsensusStrategyProfiles.RequiredHistory - 5
                                ? (index - ApprovedConsensusStrategyProfiles.RequiredHistory + 5) * .25m
                                : 0m);
                    if (interval == CandleInterval.FourHours
                        && index == ApprovedConsensusStrategyProfiles.RequiredHistory - 2)
                        price -= .4m;
                    return new Candle(symbol, interval, open, open.Add(duration),
                        price, price + 1m, price - 1m, price,
                        20_000m, true, false);
                })
                .Where(candle => candle.OpenTimeUtc >= sinceUtc)
                .ToArray();
            return Task.FromResult<IReadOnlyList<Candle>>(series);
        }
    }

    private sealed class RisingHistorySource(DateTimeOffset now, int pairCount, bool rising = true) :
        ITradablePairSource,
        IHistoricalCandleSource
    {
        private readonly string[] _symbols = Enumerable.Range(1, pairCount)
            .Select(index => $"COIN{index:D2}/EUR")
            .ToArray();

        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>(_symbols.Select(symbol =>
            {
                var parts = symbol.Split('/');
                return new TradablePair(
                    symbol.Replace("/", string.Empty, StringComparison.Ordinal),
                    symbol,
                    parts[0],
                    parts[1],
                    true,
                    0.0001m,
                    0.0001m,
                    0.0001m,
                    10m);
            }).ToArray());

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interval == CandleInterval.OneDay)
            {
                var close = AlignDown(now, TimeSpan.FromDays(1));
                return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                    .Select(index => Candle(
                        symbol,
                        interval,
                        close.AddDays(index - ApprovedConsensusStrategyProfiles.RequiredHistory),
                        Price(index),
                        20_000m))
                    .ToArray());
            }

            var duration = TimeSpan.FromMinutes((int)interval);
            var closeUtc = AlignDown(now, duration);
            return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                .Select(index =>
                {
                    var open = closeUtc.AddTicks(duration.Ticks * (index - ApprovedConsensusStrategyProfiles.RequiredHistory));
                    var price = Price(index);
                    return Candle(
                        symbol,
                        interval,
                        open,
                        price,
                        index == ApprovedConsensusStrategyProfiles.RequiredHistory - 1 ? 200m : 100m);
                })
                .ToArray());
        }

        private decimal Price(int index) => rising ? 100m + index : 500m - index;

        private static Candle Candle(
            string symbol,
            CandleInterval interval,
            DateTimeOffset open,
            decimal price,
            decimal volume)
        {
            var duration = TimeSpan.FromMinutes((int)interval);
            return new Candle(
                symbol,
                interval,
                open,
                open.Add(duration),
                price,
                price + 1m,
                price - 1m,
                price + 0.5m,
                volume,
                true,
                false);
        }
    }
}
