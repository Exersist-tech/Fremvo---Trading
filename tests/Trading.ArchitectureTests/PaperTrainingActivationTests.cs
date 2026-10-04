using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingActivationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedAuditActions = ["PaperTrainingStarted"];
    private static readonly int[] ExpectedGroupSizes = [4, 3, 3];
    private static readonly string[] ExpectedCatalogSymbols =
        ["XRP/EUR", "TRX/EUR", "DOGE/EUR", "ADA/EUR", "XRP/EUR", "TRX/EUR", "XBT/EUR", "XBT/EUR", "DOGE/EUR", "ADA/EUR"];

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
    public async Task ScannerStartAllocatesNoWaitingWorkers()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var audit = new InMemoryAuditEventWriter();
        var service = new PaperTrainingActivationService(repository, audit, new FixedTimeProvider());
        var owner = Guid.NewGuid();

        var active = await service.StartScannerAsync(
            owner,
            owner,
            RoleType.User,
            CompletePrerequisites());

        Assert.True(active.IsActive);
        Assert.Empty(active.Slots);
        Assert.Equal(ExperimentWorker.MaxWorkersPerUser, active.ConfiguredStrategies.Count);
        Assert.Equal(
            Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser),
            active.ConfiguredStrategies.Select(assignment => assignment.Slot));
        Assert.Empty(active.QualificationResults);
        Assert.Equal(ExpectedAuditActions, audit.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task PaperStartRejectsExpiredOrOverCapacityEntitlementsWithoutPersistingActivation()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var audit = new InMemoryAuditEventWriter();
        var owner = Guid.NewGuid();
        var limit = new FixedPaperWorkerAdmissionLimit(1);
        var service = new PaperTrainingActivationService(repository, audit, new FixedTimeProvider(), limit);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(
            owner, owner, RoleType.User, 2, CompletePrerequisites()));
        Assert.Null(await repository.GetAsync(owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartQualifiedAsync(
            owner, owner, RoleType.User, PaperTrainingActivationService.ApprovedSlots.Take(2).ToArray(),
            [], CompletePrerequisites()));
        Assert.Null(await repository.GetAsync(owner));
        limit.Maximum = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartScannerAsync(
            owner, owner, RoleType.User, CompletePrerequisites()));
        Assert.Null(await repository.GetAsync(owner));
        Assert.Empty(audit.Events);

        limit.Maximum = 1;
        Assert.True((await service.StartAsync(owner, owner, RoleType.User, 1,
            CompletePrerequisites())).IsActive);
        await service.DisableAsync(owner, owner, RoleType.User);
        limit.Maximum = 2;
        Assert.True((await service.StartAsync(owner, owner, RoleType.User, 2,
            CompletePrerequisites())).IsActive);
        await service.DisableAsync(owner, owner, RoleType.User);
        limit.Maximum = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartScannerAsync(
            owner, owner, RoleType.User, CompletePrerequisites()));
        Assert.Equal(2, (await repository.GetAsync(owner))!.Slots.Count);
    }

    private sealed class FixedPaperWorkerAdmissionLimit(int maximum) : IPaperWorkerAdmissionLimit
    {
        public int Maximum { get; set; } = maximum;
        public Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default) => Task.FromResult(Maximum);
    }

    [Fact]
    public async Task WorkerStrategyAssignmentsCanChangeWhileActiveWithReservedTradesAndSurviveRestart()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var audit = new InMemoryAuditEventWriter();
        var service = new PaperTrainingActivationService(repository, audit, new FixedTimeProvider());
        var owner = Guid.NewGuid();
        var active = await service.StartScannerAsync(owner, owner, RoleType.User, CompletePrerequisites());
        var assignments = Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser)
            .Select(slot =>
            {
                var strategyId = slot == 1
                    ? PaperTrainingActivationService.ThreeSwingChannelDivergenceStrategyId
                    : PaperTrainingActivationService.ApprovedSlots[0].StrategyId;
                var settings = slot == 1 ? "{}" : """{"regimeFastEma":40}""";
                return new PaperTrainingStrategyAssignment(slot, strategyId, settings);
            })
            .ToArray();

        var reservedSlot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "BTC/USD",
            ProvenanceId = "scan-1234567890ABCDEF12345678"
        };
        Assert.True(await repository.TrySaveAsync(active with { Slots = [reservedSlot] }, active.State));
        var strategySettings = ApprovedStrategyParameters.Defaults
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        strategySettings["platform.ema-trend-continuation"] = """{"regimeFastEma":40}""";
        strategySettings["platform.bollinger-mean-reversion"] = """{"bollingerPeriod":30}""";
        var configurationService = new PaperTrainingActivationService(
            repository, audit, new FixedTimeProvider(Now.AddMinutes(1)));
        var configured = await configurationService.ConfigureScannerStrategiesAsync(
            owner,
            owner,
            RoleType.User,
            assignments,
            strategySettings);
        var normalizedAssignments = assignments.Select(assignment =>
        {
            Assert.True(ApprovedStrategyParameters.TryNormalize(
                assignment.StrategyId,
                assignment.StrategyParameters,
                out var normalized,
                out var error), error);
            return assignment with { StrategyParameters = normalized };
        }).ToArray();
        Assert.Equal(normalizedAssignments, configured.ConfiguredStrategies);
        Assert.Equal(40, ApprovedStrategyParameters.Read(
            "platform.ema-trend-continuation",
            configured.ConfiguredStrategyParameters["platform.ema-trend-continuation"]).Int32("regimeFastEma"));
        Assert.Equal(30, ApprovedStrategyParameters.Read(
            "platform.bollinger-mean-reversion",
            configured.ConfiguredStrategyParameters["platform.bollinger-mean-reversion"]).Int32("bollingerPeriod"));
        Assert.Equal([reservedSlot], configured.Slots);
        Assert.True(configured.IsActive);
        Assert.Equal(active.ChangedAtUtc, configured.ChangedAtUtc);
        Assert.Contains(audit.Events, auditEvent =>
            auditEvent.Action == "PaperTrainingWorkerStrategiesConfigured"
            && auditEvent.OccurredAtUtc == Now.AddMinutes(1));

        await service.DisableAsync(owner, owner, RoleType.User);
        var restarted = await service.StartScannerAsync(owner, owner, RoleType.User, CompletePrerequisites());
        Assert.Equal(normalizedAssignments, restarted.ConfiguredStrategies);
        Assert.Equal(
            configured.ConfiguredStrategyParameters.OrderBy(item => item.Key),
            restarted.ConfiguredStrategyParameters.OrderBy(item => item.Key));
        Assert.True(restarted.IsActive);
    }

    [Fact]
    public async Task StrategySettingsForUnassignedFamiliesPersistInTheExistingActivationRow()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;
        var owner = Guid.NewGuid();
        var settings = ApprovedStrategyParameters.Defaults
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        settings["platform.three-swing-channel-divergence"] = """{"channelPeriod":60}""";
        await using var db = new TradingDbContext(options);
        var repository = new EfPaperTrainingActivationRepository(db);
        var activation = new PaperTrainingActivation(
        owner,
        PaperTrainingActivationState.Disabled,
        [],
        CompletePrerequisites(),
        Now,
        owner,
        StrategyAssignments: Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser)
            .Select(slot => new PaperTrainingStrategyAssignment(
                slot,
                PaperTrainingActivationService.ApprovedSlots[(slot - 1) % PaperTrainingActivationService.ApprovedSlots.Count].StrategyId))
            .ToArray(),
        StrategyParameterSettings: settings);

        Assert.True(await repository.TrySaveAsync(activation, null));
        var restored = await repository.GetAsync(owner);

        Assert.NotNull(restored);
        Assert.Equal(60, ApprovedStrategyParameters.Read(
        "platform.three-swing-channel-divergence",
        restored!.ConfiguredStrategyParameters["platform.three-swing-channel-divergence"]).Int32("channelPeriod"));
    }

    [Fact]
    public async Task OwnerReviewedRelativeProtectionSettingsSurviveActivationReload()
    {
        const string family = "platform.relative-strength-pullback-rotation";
        var owner = Guid.NewGuid();
        var settings = ApprovedStrategyParameters.Defaults.ToDictionary(
            entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        Assert.True(ApprovedStrategyParameters.TryNormalize(family,
            """{"relativeRankingModel":"dailyExcessBreadth","relativePlanModel":"fourHourStructure","stopSwingLookback":8,"stopBufferAtr":0.3,"targetChannelLookback":24,"minimumRewardRisk":1.2}""",
            out var reviewed, out var error), error);
        settings[family] = reviewed;
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var repository = new EfPaperTrainingActivationRepository(db);
        var activation = new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Disabled, [],
            CompletePrerequisites(), Now, owner,
            StrategyAssignments: Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser)
                .Select(slot => new PaperTrainingStrategyAssignment(slot,
                    slot == 1 ? family : PaperTrainingActivationService.ApprovedSlots[0].StrategyId,
                    settings[slot == 1 ? family : PaperTrainingActivationService.ApprovedSlots[0].StrategyId]))
                .ToArray(),
            StrategyParameterSettings: settings);
        Assert.True(await repository.TrySaveAsync(activation, null));

        var restored = await repository.GetAsync(owner);
        Assert.NotNull(restored);
        Assert.Equal(reviewed, restored!.ConfiguredStrategyParameters[family]);
        Assert.Equal(reviewed, restored.ConfiguredStrategies[0].StrategyParameters);
        Assert.Equal(8, ApprovedStrategyParameters.Read(family, reviewed).Int32("stopSwingLookback"));
        Assert.Equal(.3m, ApprovedStrategyParameters.Read(family, reviewed).Decimal("stopBufferAtr"));
        Assert.Null(await repository.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task WorkerVersionSurvivesActivationPersistenceWithoutNewSqlColumns()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var owner = Guid.NewGuid();
        await using var db = new TradingDbContext(options);
        var repository = new EfPaperTrainingActivationRepository(db);
        var original = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            StrategyVersion = 3,
            ProvenanceId = "scan-versioned-paper-worker"
        };
        var activation = new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Disabled, [original],
            CompletePrerequisites(), Now, owner);

        Assert.True(await repository.TrySaveAsync(activation, null));
        var restored = await repository.GetAsync(owner);
        Assert.Equal(original, Assert.Single(restored!.Slots));

        var legacyJson = System.Text.Json.JsonSerializer.Serialize(
            original with { StrategyVersion = 2 })
            .Replace(",\"StrategyVersion\":2", "", StringComparison.Ordinal);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PaperTrainingWorkerSlot>(legacyJson);
        Assert.Equal(2, legacy!.StrategyVersion);
    }

    [Fact]
    public async Task SelectedRegimeComponentSurvivesActivationPersistence()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var owner = Guid.NewGuid();
        await using var db = new TradingDbContext(options);
        var repository = new EfPaperTrainingActivationRepository(db);
        var slot = PaperTrainingActivationService.CreateApprovedWorkerTemplate(
            "platform.regime-switching-ensemble") with
        {
            StrategyVersion = 4,
            ProvenanceId = "scan-pinned-component",
            PairFilters = new PaperExchangeFilters(.01m, .0001m, .001m, 5m),
            RankingUniverseSymbols = ["ETH/EUR", "XBT/EUR"],
            SelectedComponent = new PaperRegimeComponentSelection(
                "platform.ema-trend-continuation", 3, """{"minimumAgreement":4}""",
                ["ETH/EUR", "XBT/EUR"], Now.AddDays(-1), Now, CandleInterval.FourHours,
                new string('A', 64))
        };
        var activation = new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Disabled, [slot],
            CompletePrerequisites(), Now, owner);

        Assert.True(await repository.TrySaveAsync(activation, null));
        var restored = Assert.Single((await repository.GetAsync(owner))!.Slots);
        Assert.Equal(4, restored.StrategyVersion);
        var selected = Assert.IsType<PaperRegimeComponentSelection>(restored.SelectedComponent);
        Assert.Equal(slot.SelectedComponent!.FamilyId, selected.FamilyId);
        Assert.Equal(slot.RankingUniverseSymbols, restored.RankingUniverseSymbols);
        Assert.Equal(slot.SelectedComponent.StrategyParameters, selected.StrategyParameters);
        Assert.Equal(slot.SelectedComponent.UniverseSymbols, selected.UniverseSymbols);
        Assert.Equal(slot.SelectedComponent.ComponentDecisionFingerprint, selected.ComponentDecisionFingerprint);
        Assert.Equal(slot.PairFilters, restored.PairFilters);
    }

    [Fact]
    public async Task RelativeStrengthAdmissionHourSurvivesRestartWithoutChangingLegacySlots()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var owner = Guid.NewGuid();
        await using var db = new TradingDbContext(options);
        var repository = new EfPaperTrainingActivationRepository(db);
        var slot = PaperTrainingActivationService.CreateApprovedWorkerTemplate(
            "platform.relative-strength-pullback-rotation") with
        {
            StrategyVersion = 5,
            AdmissionCloseUtc = Now.AddHours(1),
            RankingUniverseSymbols = ["ETH/EUR", "XBT/EUR"]
        };
        Assert.True(await repository.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Disabled, [slot],
            CompletePrerequisites(), Now, owner), null));

        var restored = Assert.Single((await repository.GetAsync(owner))!.Slots);
        Assert.Equal(slot.AdmissionCloseUtc, restored.AdmissionCloseUtc);
        Assert.Equal(slot.RankingUniverseSymbols, restored.RankingUniverseSymbols);
        using var serialized = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(slot with { StrategyVersion = 2 }));
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(
            serialized.RootElement.EnumerateObject()
                .Where(property => property.Name != "AdmissionCloseUtc")
                .ToDictionary(property => property.Name, property => property.Value));
        Assert.DoesNotContain("\"AdmissionCloseUtc\"", legacyJson, StringComparison.Ordinal);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<PaperTrainingWorkerSlot>(legacyJson);
        Assert.Null(legacy!.AdmissionCloseUtc);
    }

    [Fact]
    public async Task WorkerStrategyConfigurationRejectsMissingSlotsAndUnapprovedStrategies()
    {
        var repository = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(repository, new InMemoryAuditEventWriter(), new FixedTimeProvider());
        var owner = Guid.NewGuid();
        await service.StartScannerAsync(owner, owner, RoleType.User, CompletePrerequisites());
        await service.DisableAsync(owner, owner, RoleType.User);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ConfigureScannerStrategiesAsync(
                owner,
                owner,
                RoleType.User,
                PaperTrainingActivationService.ApprovedSlots
                    .Take(ExperimentWorker.MaxWorkersPerUser - 1)
                    .Select(slot => new PaperTrainingStrategyAssignment(slot.Slot, slot.StrategyId))
                    .ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ConfigureScannerStrategiesAsync(
                owner,
                owner,
                RoleType.User,
                Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser)
                    .Select(slot => new PaperTrainingStrategyAssignment(slot, "user.supplied.strategy"))
                    .ToArray()));
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
        Assert.Equal(10, PaperTrainingActivationService.ApprovedSlots.Count);
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
        var profile = ApprovedConsensusStrategyProfiles.Resolve(
            discovered.StrategyId,
            discovered.Interval);
        Assert.Equal(
            new[] { profile.Signal, profile.Execution, profile.Regime }.Distinct().Order(),
            (await repository.GetActiveSubscriptionsAsync())
                .Where(subscription => subscription.Symbol == "ETH/USD")
                .Select(subscription => subscription.Interval)
                .Order());
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

    private sealed class FixedTimeProvider(DateTimeOffset? utcNow = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow ?? Now;
    }
}
