using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingMonitorTests
{
    [Fact]
    public async Task ShowsAllConfiguredStrategySlotsAsScanningWithoutPairAssignments()
    {
        var ownerId = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new Trading.Application.Pipeline.InMemoryAuditEventWriter(),
            TimeProvider.System);
        await service.StartScannerAsync(
            ownerId,
            ownerId,
            Trading.Domain.Identity.RoleType.User,
            new(true, true, true, true, true, true));

        var monitor = await new PaperTrainingMonitorService(
            activations,
            new InMemoryExperimentWorkerRepository(),
            new StubCandleRepository(), new InMemoryExperimentPaperExecutionLedger(),
            new EmptyPaperPlans()).GetAsync(ownerId);

        Assert.Equal(ExperimentWorker.MaxWorkersPerUser, monitor.Workers.Count);
        Assert.All(monitor.Workers, item =>
        {
            Assert.Equal("Scanning", item.RuntimeStatus);
            Assert.Equal(string.Empty, item.Symbol);
            Assert.Equal(CandleInterval.None, item.Interval);
            Assert.Null(item.WorkerId);
            Assert.Null(item.PositionQuantity);
        });
    }

    [Fact]
    public async Task ShowsApprovedThreeSwingAssignmentsInVacantSlotsWithTheirOwnSeeds()
    {
        var ownerId = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), TimeProvider.System);
        var active = await service.StartScannerAsync(
            ownerId, ownerId, Trading.Domain.Identity.RoleType.User,
            new(true, true, true, true, true, true));
        var strategyId = PaperTrainingActivationService.ThreeSwingChannelDivergenceStrategyId;
        var assignments = active.ConfiguredStrategies.Select(assignment =>
            assignment.Slot is 3 or 7
                ? assignment with { StrategyId = strategyId }
                : assignment).ToArray();
        await service.ConfigureScannerStrategiesAsync(
            ownerId, ownerId, Trading.Domain.Identity.RoleType.User, assignments);

        var monitor = await new PaperTrainingMonitorService(
            activations, new InMemoryExperimentWorkerRepository(),
            new StubCandleRepository(), new InMemoryExperimentPaperExecutionLedger(),
            new EmptyPaperPlans()).GetAsync(ownerId);

        Assert.Equal(ExperimentWorker.MaxWorkersPerUser, monitor.Workers.Count);
        foreach (var slotNumber in new[] { 3, 7 })
        {
            var item = Assert.Single(monitor.Workers, worker => worker.Slot == slotNumber);
            Assert.Equal(strategyId, item.StrategyId);
            Assert.Equal(PaperTrainingActivationService.ApprovedSlots[slotNumber - 1].Seed, item.Seed);
            Assert.Equal("Scanning", item.RuntimeStatus);
            Assert.Null(item.WorkerId);
        }
    }

    [Fact]
    public async Task ListsOnlyCurrentOwnerActiveSlotsWithBalancesPositionsAndRecentTrades()
    {
        var ownerId = Guid.NewGuid();
        var changedAtUtc = new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "XBT/EUR",
            StartingCash = 1_000m,
            Interval = Trading.Domain.Market.CandleInterval.FiveMinutes
        };
        var qualification = new PaperTrainingQualificationResult(
            slot.Slot,
            slot.Symbol,
            false,
            -1m,
            2,
            3m,
            new string('A', 64),
            "Unqualified paper exploration only.",
            slot.StrategyId,
            PaperOnlyExploration: true,
            Interval: slot.Interval);
        var scannerRecords = new[]
        {
            new PaperTrainingQualificationResult(
                0, "KRAKEN/EUR", true, 0m, 0, 0m, new string('B', 64),
                "Scanner run.", "platform.scanner", true, CandleInterval.FiveMinutes),
            new PaperTrainingQualificationResult(
                0, "XBT/EUR", false, 0m, 0, 0m, new string('C', 64),
                "Universe member.", "platform.scanner-universe", false, CandleInterval.OneDay)
        };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                changedAtUtc,
                ownerId,
                [qualification, .. scannerRecords]),
            null);
        var workers = new InMemoryExperimentWorkerRepository();
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            ownerId,
            $"Paper training {slot.Slot} {changedAtUtc:yyyyMMddHHmmssfffffff}",
            slot.StrategyId,
            slot.Symbol,
            slot.StartingCash,
            changedAtUtc,
            slot.Seed);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 1m, "buy", changedAtUtc.AddHours(1));
        worker.ApplyPaperTrade(0.5m, 110m, 1m, "sell", changedAtUtc.AddHours(2));
        await workers.SaveAsync(worker);
        await workers.SaveAsync(new ExperimentWorker(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Another owner's worker",
            slot.StrategyId,
            slot.Symbol,
            1_000m,
            changedAtUtc,
            7));

        var currentPriceAt = changedAtUtc.AddHours(3);
        var priceCandle = new Candle(
            slot.Symbol,
            CandleInterval.FiveMinutes,
            currentPriceAt.AddMinutes(-5),
            currentPriceAt,
            119m,
            121m,
            118m,
            120m,
            10m,
            true,
            false);
        var candles = new StubCandleRepository(priceCandle);
        var monitor = await new PaperTrainingMonitorService(activations, workers, candles,
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(currentPriceAt)).GetAsync(ownerId);

        var item = Assert.Single(monitor.Workers);
        Assert.Equal(PaperTrainingMonitorQualification.Exploration, item.Qualification);
        Assert.Equal(slot.Interval, item.Interval);
        Assert.Equal(
            [CandleInterval.OneHour, CandleInterval.FiveMinutes, CandleInterval.OneMinute],
            item.AnalysisIntervals);
        Assert.Equal("Unprotected", item.RuntimeStatus);
        Assert.Null(item.ProtectiveStopPrice);
        Assert.Null(item.EstimatedTargetPrice);
        Assert.Contains("matching approved protective plan", item.FailureReason, StringComparison.Ordinal);
        Assert.Equal(953m, item.CashBalance);
        Assert.Equal(0.5m, item.PositionQuantity);
        Assert.Equal(101m, item.AverageEntryPrice);
        Assert.Equal(100m, item.OpenBuyFillPrice);
        Assert.Equal(100m, item.LastBuyFillPrice);
        Assert.Equal(110m, item.LastSellFillPrice);
        Assert.Equal(50.5m, item.PositionCost);
        Assert.Equal(120m, item.CurrentPrice);
        Assert.Equal(currentPriceAt, item.CurrentPriceAsOfUtc);
        Assert.Equal(60m, item.PositionMarketValue);
        Assert.Equal(9.5m, item.UnrealizedProfitAndLoss);
        Assert.Equal(3.5m, item.RealizedProfitAndLoss);
        Assert.Equal(0, item.AdditionCount);
        Assert.Equal(3, item.MaximumAdditions);
        Assert.Equal(2, item.TradeCount);
        Assert.Equal(["sell", "buy"], item.RecentTrades.Select(trade => trade.Direction));
        Assert.Equal(["XBT/EUR", "XBT/EUR"], item.RecentTrades.Select(trade => trade.Symbol));

        var lastValidMonitor = await new PaperTrainingMonitorService(activations, workers, candles,
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(currentPriceAt.Add(PaperTrainingMonitorService.MaximumValuationAge))).GetAsync(ownerId);
        Assert.Equal(120m, Assert.Single(lastValidMonitor.Workers).CurrentPrice);

        var oldPriceMonitor = await new PaperTrainingMonitorService(activations, workers, candles,
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(currentPriceAt.Add(PaperTrainingMonitorService.MaximumValuationAge).AddTicks(1))).GetAsync(ownerId);
        var unpriced = Assert.Single(oldPriceMonitor.Workers);
        Assert.Null(unpriced.CurrentPrice);
        Assert.Equal(currentPriceAt, unpriced.CurrentPriceAsOfUtc);
        Assert.Null(unpriced.PositionMarketValue);
        Assert.Null(unpriced.UnrealizedProfitAndLoss);

        var futurePriceMonitor = await new PaperTrainingMonitorService(activations, workers, candles,
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(currentPriceAt.AddMinutes(-1))).GetAsync(ownerId);
        Assert.Null(Assert.Single(futurePriceMonitor.Workers).UnrealizedProfitAndLoss);

        var planAt = changedAtUtc.AddHours(1);
        var key = new ExperimentDecisionKey(ownerId, worker.Id, 1, ExperimentResearchGroup.A,
            slot.StrategyId, 1, new string('A', 64), slot.Symbol, slot.Interval,
            planAt.AddMinutes(-5), planAt, planAt);
        var plans = new EmptyPaperPlans([new ExperimentPaperPlanEvidence(key, 90m, 130m, planAt)]);
        var protectedMonitor = await new PaperTrainingMonitorService(activations, workers, candles,
            new InMemoryExperimentPaperExecutionLedger(),
            plans, new FixedTimeProvider(currentPriceAt))
            .GetAsync(ownerId);
        var stale = Assert.Single(protectedMonitor.Workers);
        Assert.Equal("ProtectionDataStale", stale.RuntimeStatus);
        Assert.Equal(90m, stale.ProtectiveStopPrice);
        Assert.Equal(130m, stale.EstimatedTargetPrice);
        Assert.Contains("one-minute", stale.FailureReason, StringComparison.Ordinal);

        var oneMinute = new Candle(slot.Symbol, CandleInterval.OneMinute,
            currentPriceAt.AddMinutes(-1), currentPriceAt, 120m, 121m, 119m, 120m, 1m, true, false);
        var freshMonitor = await new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(priceCandle, [priceCandle, oneMinute]),
            new InMemoryExperimentPaperExecutionLedger(), plans, new FixedTimeProvider(currentPriceAt))
            .GetAsync(ownerId);
        Assert.Equal("Running", Assert.Single(freshMonitor.Workers).RuntimeStatus);
        Assert.Equal(130m, Assert.Single(freshMonitor.Workers).EstimatedTargetPrice);

        var noTarget = new EmptyPaperPlans([new ExperimentPaperPlanEvidence(key, 90m, null, planAt)]);
        var noTargetMonitor = await new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(priceCandle, [priceCandle, oneMinute]),
            new InMemoryExperimentPaperExecutionLedger(), noTarget,
            new FixedTimeProvider(currentPriceAt)).GetAsync(ownerId);
        Assert.Equal(90m, Assert.Single(noTargetMonitor.Workers).ProtectiveStopPrice);
        Assert.Null(Assert.Single(noTargetMonitor.Workers).EstimatedTargetPrice);

        var commandId = worker.Ledger.Last().Id;
        var pending = new ExperimentPaperExecutionAssociation(key, "matched-command",
            ExperimentPaperExecutionStatus.Claimed);
        var executionRecords = new InMemoryExperimentPaperExecutionLedger();
        await executionRecords.ClaimAsync(ownerId, pending);
        await executionRecords.CompleteAsync(ownerId, pending with
        {
            Status = ExperimentPaperExecutionStatus.Unknown,
            ExecutionCommandId = commandId
        });
        var frozenMonitor = await new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(priceCandle, [priceCandle, oneMinute]),
            executionRecords, plans, new FixedTimeProvider(currentPriceAt))
            .GetAsync(ownerId);
        Assert.Equal([commandId], Assert.Single(frozenMonitor.Workers)
            .UnresolvedExecution!.MatchingWorkerLedgerIds);
        Assert.False(Assert.Single(frozenMonitor.Workers)
            .UnresolvedExecution!.PortfolioEvidenceChecked);
        Assert.False(Assert.Single(frozenMonitor.Workers)
            .UnresolvedExecution!.AuditEvidenceChecked);
        Assert.False(Assert.Single(frozenMonitor.Workers)
            .UnresolvedExecution!.ExecutionEvidenceChecked);
    }

    [Fact]
    public async Task ShowsAnActivatedSlotWhileItsWorkerIsStillStarting()
    {
        var ownerId = Guid.NewGuid();
        var slot = PaperTrainingActivationService.ApprovedSlots[0];
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero),
                ownerId),
            null);

        var monitor = await new PaperTrainingMonitorService(
            activations,
            new InMemoryExperimentWorkerRepository(),
            new StubCandleRepository(), new InMemoryExperimentPaperExecutionLedger(),
            new EmptyPaperPlans()).GetAsync(ownerId);

        var item = Assert.Single(monitor.Workers);
        Assert.Null(item.WorkerId);
        Assert.Equal("WaitingForWorker", item.RuntimeStatus);
        Assert.Null(item.CashBalance);
        Assert.Null(item.CurrentPrice);
        Assert.Null(item.OpenBuyFillPrice);
        Assert.Null(item.LastBuyFillPrice);
        Assert.Null(item.LastSellFillPrice);
        Assert.Null(item.PositionMarketValue);
        Assert.Null(item.UnrealizedProfitAndLoss);
        Assert.Null(item.AdditionCount);
        Assert.Null(item.MaximumAdditions);
        Assert.Empty(item.RecentTrades);
    }

    [Fact]
    public async Task ValuesOpenWorkerFromFreshClosedOneMinuteCandleWhenFiveMinuteMarkIsStale()
    {
        var ownerId = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with { Symbol = "XBT/EUR" };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(new(
            ownerId, PaperTrainingActivationState.Active, [slot],
            new(true, true, true, true, true, true), started, ownerId), null);
        var worker = new ExperimentWorker(Guid.NewGuid(), ownerId,
            $"Paper training {slot.Slot} {started:yyyyMMddHHmmssfffffff}",
            slot.StrategyId, slot.Symbol, 1_000m, started, slot.Seed);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 1m, "buy", started.AddMinutes(1));
        var workers = new InMemoryExperimentWorkerRepository();
        await workers.SaveAsync(worker);
        var fiveMinute = new Candle(slot.Symbol, CandleInterval.FiveMinutes,
            started, started.AddMinutes(5), 100m, 101m, 99m, 100m, 1m, true, false);
        var oneMinute = new Candle(slot.Symbol, CandleInterval.OneMinute,
            started.AddMinutes(14), started.AddMinutes(15), 105m, 111m, 104m, 110m, 1m, true, false);
        var monitor = await new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(fiveMinute, [fiveMinute, oneMinute]),
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(started.AddMinutes(16))).GetAsync(ownerId);

        var item = Assert.Single(monitor.Workers);
        Assert.Equal(110m, item.CurrentPrice);
        Assert.Equal(oneMinute.CloseTimeUtc, item.CurrentPriceAsOfUtc);
        Assert.Equal(9m, item.UnrealizedProfitAndLoss);

        var beforeOneMinuteClose = await new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(fiveMinute, [fiveMinute, oneMinute]),
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(started.AddMinutes(14))).GetAsync(ownerId);
        Assert.Equal(100m, Assert.Single(beforeOneMinuteClose.Workers).CurrentPrice);

        var stale = await new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(fiveMinute, [fiveMinute, oneMinute]),
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(started.AddMinutes(26))).GetAsync(ownerId);
        Assert.Null(Assert.Single(stale.Workers).UnrealizedProfitAndLoss);
    }

    [Fact]
    public async Task ShowsOnlyCurrentOpenPositionBuyFillAverageAfterRoundTripsAndPartialExits()
    {
        var owner = Guid.NewGuid();
        var started = new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with { Symbol = "XBT/EUR" };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(new(owner, PaperTrainingActivationState.Active, [slot],
            new(true, true, true, true, true, true), started, owner), null);
        var worker = new ExperimentWorker(Guid.NewGuid(), owner,
            $"Paper training {slot.Slot} {started:yyyyMMddHHmmssfffffff}",
            slot.StrategyId, slot.Symbol, 1_000m, started, slot.Seed);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 1m, "buy", started.AddMinutes(1));
        worker.ApplyPaperTrade(1m, 110m, 1m, "sell", started.AddMinutes(2));
        worker.ApplyPaperTrade(1m, 120m, 1m, "buy", started.AddMinutes(3));
        worker.RecordFavorablePaperMark(130m);
        worker.ApplyPaperTrade(1m, 130m, 1m, "buy", started.AddMinutes(4));
        var workers = new InMemoryExperimentWorkerRepository();
        await workers.SaveAsync(worker);
        var monitor = new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(), new InMemoryExperimentPaperExecutionLedger(),
            new EmptyPaperPlans(), new FixedTimeProvider(started.AddMinutes(6)));

        var beforePartialExit = Assert.Single((await monitor.GetAsync(owner)).Workers);
        Assert.Equal(125m, beforePartialExit.OpenBuyFillPrice);
        Assert.Null(beforePartialExit.LastSellFillPrice);
        Assert.Equal(started.AddMinutes(3), beforePartialExit.OpenPositionEntry?.OccurredAtUtc);
        Assert.Equal(120m, beforePartialExit.OpenPositionEntry?.ExecutionPrice);

        worker.ApplyPaperTrade(0.5m, 140m, 0.5m, "sell", started.AddMinutes(5));
        await workers.SaveAsync(worker);
        var open = Assert.Single((await monitor.GetAsync(owner)).Workers);
        Assert.Equal(1.5m, open.PositionQuantity);
        Assert.Equal(125m, open.OpenBuyFillPrice);
        Assert.Equal(126m, open.AverageEntryPrice);
        Assert.Equal(130m, open.LastBuyFillPrice);
        Assert.Equal(140m, open.LastSellFillPrice);
        Assert.Equal(started.AddMinutes(3), open.OpenPositionEntry?.OccurredAtUtc);

        worker.ApplyPaperTrade(1.5m, 145m, 1m, "sell", started.AddMinutes(6));
        await workers.SaveAsync(worker);
        var flat = Assert.Single((await monitor.GetAsync(owner)).Workers);
        Assert.Null(flat.OpenBuyFillPrice);
        Assert.Null(flat.OpenPositionEntry);
        Assert.Null(flat.ProtectiveStopPrice);
        Assert.Null(flat.EstimatedTargetPrice);
        Assert.Equal(130m, flat.LastBuyFillPrice);
        Assert.Equal(145m, flat.LastSellFillPrice);

        worker.ApplyPaperTrade(2m, 100m, 0m, "buy", started.AddMinutes(7));
        for (var index = 0; index < 11; index++)
            worker.ApplyPaperTrade(0.1m, 110m, 0m, "sell", started.AddMinutes(8 + index));
        await workers.SaveAsync(worker);
        var later = new PaperTrainingMonitorService(activations, workers,
            new StubCandleRepository(), new InMemoryExperimentPaperExecutionLedger(),
            new EmptyPaperPlans(), new FixedTimeProvider(started.AddMinutes(30)));
        var position = Assert.Single((await later.GetAsync(owner)).Workers);
        Assert.Equal(started.AddMinutes(7), position.OpenPositionEntry?.OccurredAtUtc);
        Assert.Equal(100m, position.OpenPositionEntry?.ExecutionPrice);
        Assert.DoesNotContain(position.RecentTrades,
            trade => trade.OccurredAtUtc == started.AddMinutes(7));
    }

    [Fact]
    public async Task ActiveStrategyEditsDoNotReplaceTheOpenWorkersStrategyOrIdentity()
    {
        var ownerId = Guid.NewGuid();
        var startedAtUtc = new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero);
        var activations = new InMemoryPaperTrainingActivationRepository();
        var activationService = new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), new FixedTimeProvider(startedAtUtc));
        var active = await activationService.StartScannerAsync(ownerId, ownerId,
            Trading.Domain.Identity.RoleType.User, new(true, true, true, true, true, true));
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with { Symbol = "BTC/USD" };
        Assert.True(await activations.TrySaveAsync(active with { Slots = [slot] }, active.State));
        var workers = new InMemoryExperimentWorkerRepository();
        var worker = new ExperimentWorker(Guid.NewGuid(), ownerId,
            $"Paper training {slot.Slot} {startedAtUtc:yyyyMMddHHmmssfffffff}",
            slot.StrategyId, slot.Symbol, slot.StartingCash, startedAtUtc, slot.Seed);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 0.8m, "buy", startedAtUtc);
        await workers.SaveAsync(worker);

        var upcomingStrategy = PaperTrainingActivationService.ThreeSwingChannelDivergenceStrategyId;
        var assignments = active.ConfiguredStrategies.Select(assignment =>
            assignment.Slot == slot.Slot
                ? new PaperTrainingStrategyAssignment(slot.Slot, upcomingStrategy)
                : assignment).ToArray();
        var editingService = new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), new FixedTimeProvider(startedAtUtc.AddMinutes(1)));
        var edited = await editingService.ConfigureScannerStrategiesAsync(ownerId, ownerId,
            Trading.Domain.Identity.RoleType.User, assignments);
        Assert.True(edited.IsActive);
        Assert.Equal(startedAtUtc, edited.ChangedAtUtc);
        Assert.Equal(upcomingStrategy, edited.ConfiguredStrategies.Single(item => item.Slot == slot.Slot).StrategyId);
        Assert.Equal([slot], edited.Slots);

        var monitor = await new PaperTrainingMonitorService(
            activations, workers, new StubCandleRepository(),
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans()).GetAsync(ownerId);
        var current = Assert.Single(monitor.Workers, item => item.Slot == slot.Slot);
        Assert.Equal(worker.Id, current.WorkerId);
        Assert.Equal(slot.StrategyId, current.StrategyId);
        Assert.Equal(slot.StrategyParameters, current.StrategyParameters);
        Assert.Equal(1m, current.PositionQuantity);
    }

    [Fact]
    public async Task CompletedFlatScannerWorkersRemainVisibleUntilExecutionIsReconciled()
    {
        var ownerId = Guid.NewGuid();
        var changedAtUtc = new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero);
        var observationId = new string('A', 64);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "XBT/EUR",
            ProvenanceId = $"scan-{observationId[..24]}"
        };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                changedAtUtc,
                ownerId,
                [
                    new PaperTrainingQualificationResult(
                        9,
                        slot.Symbol,
                        true,
                        80m,
                        0,
                        0m,
                        observationId,
                        "Actionable BUY admitted.",
                        slot.StrategyId,
                        PaperOnlyExploration: true,
                        slot.Interval)
                ]),
            null);
        var workers = new InMemoryExperimentWorkerRepository();
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            ownerId,
            ContinuousPaperOpportunityScanner.WorkerName(slot),
            slot.StrategyId,
            slot.Symbol,
            slot.StartingCash,
            changedAtUtc,
            slot.Seed);
        worker.Start();
        await workers.SaveAsync(worker);

        var executions = new InMemoryExperimentPaperExecutionLedger();
        var commandRecords = new InMemoryExecutionCommandRepository();
        var executionResults = new InMemoryPaperExecutionResultRepository();
        var portfolioRecords = new InMemoryPortfolioUpdateRepository();
        var auditRecords = new StubPaperAuditEvidence(ownerId, "monitor", ["Trade.ExecutionUnknown"]);
        var service = new PaperTrainingMonitorService(
            activations,
            workers,
            new StubCandleRepository(),
            executions,
            new EmptyPaperPlans(), commands: commandRecords, portfolioUpdates: portfolioRecords,
            auditEvidence: auditRecords, executionResults: executionResults);
        var activeMonitor = await service.GetAsync(ownerId);

        Assert.Equal(PaperTrainingMonitorQualification.Qualified, Assert.Single(activeMonitor.Workers).Qualification);

        worker.Complete();
        await workers.SaveAsync(worker);

        var key = new ExperimentDecisionKey(ownerId, worker.Id, 1, ExperimentResearchGroup.A,
            slot.StrategyId, 1, observationId, slot.Symbol, slot.Interval,
            changedAtUtc.AddMinutes(-5), changedAtUtc, changedAtUtc);
        var claim = new ExperimentPaperExecutionAssociation(key, "monitor", ExperimentPaperExecutionStatus.Claimed);
        await executions.ClaimAsync(ownerId, claim);
        var blocked = Assert.Single((await service.GetAsync(ownerId)).Workers);
        Assert.Equal("RequiresReconciliation", blocked.RuntimeStatus);
        Assert.Contains("Verify the fill", blocked.FailureReason, StringComparison.Ordinal);
        Assert.Equal("Claimed", blocked.UnresolvedExecution?.Status);
        Assert.Equal("monitor", blocked.UnresolvedExecution?.CorrelationId);
        Assert.Equal(key.AsOfUtc, blocked.UnresolvedExecution?.DecisionAsOfUtc);
        Assert.Null(blocked.UnresolvedExecution?.ExecutionCommandId);
        Assert.True(blocked.UnresolvedExecution?.CommandEvidenceChecked);
        Assert.Empty(blocked.UnresolvedExecution!.RecordedCommandIds);
        Assert.True(blocked.UnresolvedExecution.PortfolioEvidenceChecked);
        Assert.Empty(blocked.UnresolvedExecution.RecordedPortfolioCommandIds);
        Assert.True(blocked.UnresolvedExecution.AuditEvidenceChecked);
        Assert.Equal(["Trade.ExecutionUnknown"], blocked.UnresolvedExecution.RecordedAuditActions);
        Assert.True(blocked.UnresolvedExecution.ExecutionEvidenceChecked);
        Assert.Empty(blocked.UnresolvedExecution.RecordedExecutions);

        var command = new ExecutionCommand(Guid.NewGuid(), Guid.NewGuid(), slot.Symbol,
            TradeDirection.Buy, 1m, 100m, changedAtUtc, "paper-monitor-command");
        await commandRecords.AddAsync(new PipelineRecord<ExecutionCommand>(
            Guid.NewGuid(), new PipelineContext(ownerId, TradingMode.Paper, "monitor"),
            PipelineStage.ExecutionCommand, command, changedAtUtc));
        await commandRecords.AddAsync(new PipelineRecord<ExecutionCommand>(
            Guid.NewGuid(), new PipelineContext(Guid.NewGuid(), TradingMode.Paper, "monitor"),
            PipelineStage.ExecutionCommand, new ExecutionCommand(Guid.NewGuid(), Guid.NewGuid(),
                slot.Symbol, TradeDirection.Buy, 1m, 100m, changedAtUtc, "foreign-paper-command"), changedAtUtc));
        Assert.Equal([command.Id], Assert.Single((await service.GetAsync(ownerId)).Workers)
            .UnresolvedExecution!.RecordedCommandIds);
        await executionResults.AddAsync(new PipelineRecord<PaperExecutionEvidence>(
            Guid.NewGuid(), new PipelineContext(ownerId, TradingMode.Paper, "monitor"),
            PipelineStage.Execution, new PaperExecutionEvidence(command.Id, ExecutionOutcome.Filled,
                1m, 100m, 0.8m, changedAtUtc), changedAtUtc));
        await executionResults.AddAsync(new PipelineRecord<PaperExecutionEvidence>(
            Guid.NewGuid(), new PipelineContext(Guid.NewGuid(), TradingMode.Paper, "monitor"),
            PipelineStage.Execution, new PaperExecutionEvidence(Guid.NewGuid(), ExecutionOutcome.Filled,
                1m, 100m, 0.8m, changedAtUtc), changedAtUtc));
        var recordedFill = Assert.Single(Assert.Single((await service.GetAsync(ownerId)).Workers)
            .UnresolvedExecution!.RecordedExecutions);
        Assert.Equal(command.Id, recordedFill.ExecutionCommandId);
        Assert.Equal(0.8m, recordedFill.Fees);
        var withoutPortfolioReader = new PaperTrainingMonitorService(
            activations, workers, new StubCandleRepository(), executions, new EmptyPaperPlans(),
            auditEvidence: auditRecords, executionResults: executionResults);
        var independentlyRead = Assert.Single((await withoutPortfolioReader.GetAsync(ownerId)).Workers)
            .UnresolvedExecution!;
        Assert.False(independentlyRead.PortfolioEvidenceChecked);
        Assert.False(independentlyRead.CommandEvidenceChecked);
        Assert.True(independentlyRead.ExecutionEvidenceChecked);
        Assert.Equal(command.Id, Assert.Single(independentlyRead.RecordedExecutions).ExecutionCommandId);
        Assert.True(independentlyRead.AuditEvidenceChecked);
        Assert.Equal(["Trade.ExecutionUnknown"], independentlyRead.RecordedAuditActions);
        var portfolio = new PortfolioUpdate(Guid.NewGuid(), command.Id, slot.Symbol,
            0m, 1m, 1_000m, 899m, 0m, 1m, changedAtUtc);
        await portfolioRecords.AddAsync(new PipelineRecord<PortfolioUpdate>(
            Guid.NewGuid(), new PipelineContext(ownerId, TradingMode.Paper, "monitor"),
            PipelineStage.PortfolioUpdate, portfolio, changedAtUtc));
        await portfolioRecords.AddAsync(new PipelineRecord<PortfolioUpdate>(
            Guid.NewGuid(), new PipelineContext(Guid.NewGuid(), TradingMode.Paper, "monitor"),
            PipelineStage.PortfolioUpdate,
            new PortfolioUpdate(Guid.NewGuid(), Guid.NewGuid(), slot.Symbol,
                0m, 1m, 1_000m, 899m, 0m, 1m, changedAtUtc), changedAtUtc));
        Assert.Equal([command.Id], Assert.Single((await service.GetAsync(ownerId)).Workers)
            .UnresolvedExecution!.RecordedPortfolioCommandIds);
        Assert.Contains("Portfolio change differs from the recorded simulated fill.",
            Assert.Single((await service.GetAsync(ownerId)).Workers).UnresolvedExecution!.EvidenceConflicts);

        await executions.CompleteAsync(ownerId, claim with { Status = ExperimentPaperExecutionStatus.Blocked });
        Assert.Empty((await service.GetAsync(ownerId)).Workers);
    }

    [Theory]
    [InlineData(EvidenceScenario.Consistent, null)]
    [InlineData(EvidenceScenario.WrongClaimId, "Recorded command identity differs from the unresolved claim.")]
    [InlineData(EvidenceScenario.WrongFee, "Worker ledger differs from the recorded simulated fill.")]
    [InlineData(EvidenceScenario.Rejected, "A rejected outcome has recorded fill, portfolio, or success evidence.")]
    [InlineData(EvidenceScenario.UnknownWithFill, null)]
    public async Task FrozenWorkerComparesAvailableEvidenceWithoutClearingClaim(
        EvidenceScenario scenario, string? expectedConflict)
    {
        var ownerId = Guid.NewGuid();
        var atUtc = new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with { Symbol = "XBT/EUR" };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(new(
            ownerId, PaperTrainingActivationState.Active, [slot],
            new(true, true, true, true, true, true), atUtc, ownerId), null);
        var workers = new InMemoryExperimentWorkerRepository();
        var worker = new ExperimentWorker(Guid.NewGuid(), ownerId,
            $"Paper training {slot.Slot} {atUtc:yyyyMMddHHmmssfffffff}",
            slot.StrategyId, slot.Symbol, 1_000m, atUtc, slot.Seed);
        var commandId = Guid.NewGuid();
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 0.8m, "buy", atUtc, commandId);
        await workers.SaveAsync(worker);

        var key = new ExperimentDecisionKey(ownerId, worker.Id, slot.Slot, ExperimentResearchGroup.A,
            slot.StrategyId, 1, new string('A', 64), slot.Symbol, slot.Interval,
            atUtc.AddMinutes(-5), atUtc, atUtc);
        var claim = new ExperimentPaperExecutionAssociation(key, "evidence-check",
            ExperimentPaperExecutionStatus.Claimed);
        var executions = new InMemoryExperimentPaperExecutionLedger();
        await executions.ClaimAsync(ownerId, claim);
        await executions.CompleteAsync(ownerId, claim with
        {
            Status = ExperimentPaperExecutionStatus.Unknown,
            ExecutionCommandId = scenario == EvidenceScenario.WrongClaimId ? Guid.NewGuid() : commandId
        });
        var commands = new InMemoryExecutionCommandRepository();
        await commands.AddAsync(new PipelineRecord<ExecutionCommand>(
            Guid.NewGuid(), new PipelineContext(ownerId, TradingMode.Paper, claim.CorrelationId),
            PipelineStage.ExecutionCommand,
            new ExecutionCommand(commandId, Guid.NewGuid(), slot.Symbol,
                TradeDirection.Buy, 1m, 100m, atUtc, "test-evidence"), atUtc));
        var results = new InMemoryPaperExecutionResultRepository();
        await results.AddAsync(new PipelineRecord<PaperExecutionEvidence>(
            Guid.NewGuid(), new PipelineContext(ownerId, TradingMode.Paper, claim.CorrelationId),
            PipelineStage.Execution, new PaperExecutionEvidence(commandId,
                scenario switch
                {
                    EvidenceScenario.Rejected => ExecutionOutcome.Rejected,
                    EvidenceScenario.UnknownWithFill => ExecutionOutcome.Unknown,
                    _ => ExecutionOutcome.Filled
                },
                scenario == EvidenceScenario.Rejected ? 0m : 1m,
                scenario == EvidenceScenario.Rejected ? 0m : 100m,
                scenario == EvidenceScenario.Rejected ? 0m
                    : scenario == EvidenceScenario.WrongFee ? 0.9m : 0.8m, atUtc), atUtc));
        var updates = new InMemoryPortfolioUpdateRepository();
        await updates.AddAsync(new PipelineRecord<PortfolioUpdate>(
            Guid.NewGuid(), new PipelineContext(ownerId, TradingMode.Paper, claim.CorrelationId),
            PipelineStage.PortfolioUpdate, new PortfolioUpdate(Guid.NewGuid(), commandId, slot.Symbol,
                0m, 1m, 1_000m, 899.2m, 0m, 0.8m, atUtc), atUtc));

        var monitor = new PaperTrainingMonitorService(
            activations, workers, new StubCandleRepository(), executions, new EmptyPaperPlans(),
            commands: commands, portfolioUpdates: updates, executionResults: results);
        var frozen = Assert.Single((await monitor.GetAsync(ownerId)).Workers);
        Assert.Equal("RequiresReconciliation", frozen.RuntimeStatus);
        Assert.Equal("Unknown", frozen.UnresolvedExecution!.Status);
        if (expectedConflict is null)
            Assert.Empty(frozen.UnresolvedExecution.EvidenceConflicts);
        else
            Assert.Contains(expectedConflict, frozen.UnresolvedExecution.EvidenceConflicts);
        if (scenario == EvidenceScenario.WrongFee)
            Assert.Contains("Portfolio change differs from the recorded simulated fill.",
                frozen.UnresolvedExecution.EvidenceConflicts);
        Assert.NotNull(await executions.GetUnresolvedAsync(ownerId, worker.Id));
    }

    [Fact]
    public async Task ScannerWorkerRemainsQualifiedAfterAdmissionEvidenceIsPruned()
    {
        var ownerId = Guid.NewGuid();
        var changedAtUtc = new DateTimeOffset(2026, 9, 23, 19, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "XBT/EUR",
            ProvenanceId = $"scan-{new string('A', 24)}"
        };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                changedAtUtc,
                ownerId,
                []),
            null);
        var workers = new InMemoryExperimentWorkerRepository();
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            ownerId,
            ContinuousPaperOpportunityScanner.WorkerName(slot),
            slot.StrategyId,
            slot.Symbol,
            slot.StartingCash,
            changedAtUtc,
            slot.Seed);
        worker.Start();
        await workers.SaveAsync(worker);

        var monitor = await new PaperTrainingMonitorService(
            activations,
            workers,
            new StubCandleRepository(), new InMemoryExperimentPaperExecutionLedger(),
            new EmptyPaperPlans()).GetAsync(ownerId);

        Assert.Equal(PaperTrainingMonitorQualification.Qualified, Assert.Single(monitor.Workers).Qualification);
    }

    [Fact]
    public async Task UsesPreviousSafeClosedPriceWhenLatestCandleIsIncomplete()
    {
        var ownerId = Guid.NewGuid();
        var changedAtUtc = new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with { Symbol = "XBT/EUR" };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                changedAtUtc,
                ownerId),
            null);
        var safe = new Candle(
            slot.Symbol,
            CandleInterval.FiveMinutes,
            changedAtUtc,
            changedAtUtc.AddMinutes(5),
            100m,
            101m,
            99m,
            100m,
            10m,
            true,
            false);
        var incomplete = new Candle(
            slot.Symbol,
            CandleInterval.FiveMinutes,
            changedAtUtc.AddMinutes(5),
            changedAtUtc.AddMinutes(10),
            100m,
            102m,
            100m,
            101m,
            4m,
            false,
            false);

        var monitor = await new PaperTrainingMonitorService(
            activations,
            new InMemoryExperimentWorkerRepository(),
            new StubCandleRepository(incomplete, [safe, incomplete]),
            new InMemoryExperimentPaperExecutionLedger(), new EmptyPaperPlans(),
            new FixedTimeProvider(safe.CloseTimeUtc)).GetAsync(ownerId);

        Assert.Equal(100m, Assert.Single(monitor.Workers).CurrentPrice);
    }

    private sealed class EmptyPaperPlans(
        IReadOnlyList<ExperimentPaperPlanEvidence>? plans = null) : IExperimentPaperPlanEvidenceRepository
    {
        public Task SaveAsync(ExperimentPaperPlanEvidence evidence, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ExperimentPaperPlanEvidence>> ListAsync(
            Guid userId, Guid workerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExperimentPaperPlanEvidence>>(
                (plans ?? []).Where(plan => plan.DecisionKey.UserId == userId
                    && plan.DecisionKey.WorkerId == workerId).ToArray());
    }

    public enum EvidenceScenario
    {
        Consistent,
        WrongClaimId,
        WrongFee,
        Rejected,
        UnknownWithFill
    }

    private sealed class StubPaperAuditEvidence(
        Guid owner, string correlation, IReadOnlyList<string> actions) : IPaperTradeAuditEvidenceReader
    {
        public Task<IReadOnlyList<string>> ListActionsByCorrelationAsync(
            Guid ownerId, string correlationId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(
                ownerId == owner && correlationId == correlation ? actions : []);
        }
    }

    private sealed class StubCandleRepository(
        Candle? latest = null,
        IReadOnlyCollection<Candle>? candles = null) : ICandleRepository
    {
        public Task<Candle?> GetLatestAsync(
            string symbol,
            CandleInterval interval,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(latest is not null
                && latest.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                && latest.Interval == interval
                    ? latest
                    : candles?.Where(candle => candle.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                        && candle.Interval == interval)
                        .OrderByDescending(candle => candle.OpenTimeUtc).FirstOrDefault());

        public Task<IReadOnlyCollection<Candle>> ListAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Candle>>((candles ?? [])
                .Where(candle => candle.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                    && candle.Interval == interval
                    && candle.OpenTimeUtc >= fromUtc
                    && candle.OpenTimeUtc <= toUtc)
                .ToArray());

        public Task<CandleWriteResult> UpsertAsync(
            Candle candle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CandleWriteResult.Inserted);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
