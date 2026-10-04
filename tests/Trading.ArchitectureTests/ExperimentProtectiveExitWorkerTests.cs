using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Risk;
using Trading.Workers.Experiments;

namespace Trading.ArchitectureTests;

public sealed class ExperimentProtectiveExitWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TriggerRoutesOnlyOnceThroughPaperPipelineAndUsesFreshClosedPrice()
    {
        var harness = new Harness(Candle(Now.AddMinutes(-1), 280m, 320m, 270m));
        var first = await harness.Orchestrator.EvaluateOwnerAsync(harness.User);
        var second = await harness.Orchestrator.EvaluateOwnerAsync(harness.User);

        Assert.True(Assert.Single(first).Submitted, first[0].Reason);
        Assert.Contains(second, result => result.Reason.Contains("already claimed", StringComparison.Ordinal));
        var fill = Assert.Single(harness.Adapter.Ledger);
        Assert.Equal(TradeDirection.Sell, fill.Direction);
        Assert.Equal(280m, fill.Price); // Both levels classify as a stop; the latest close is the paper reference.
        Assert.Single(await harness.Commands.ListForUserAsync(harness.User));
    }

    [Fact]
    public async Task DurableProtectiveClaimSharesPaperExecutionInsteadOfFreezingItsOwnExit()
    {
        var candle = Candle(Now.AddMinutes(-1), 280m, 320m, 270m);
        var harness = new Harness(new FixedCandles(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [candle]))),
            sharedPaperClaim: true);

        var first = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));
        Assert.True(first.Submitted, first.Reason);
        var fill = Assert.Single(harness.Adapter.Ledger);
        var command = Assert.Single(await harness.Commands.ListForUserAsync(harness.User));
        Assert.Equal(command.Payload.Id, fill.ExecutionCommandId);
        Assert.Equal(0m, harness.Worker.PositionQuantity);
        Assert.Equal(command.Payload.Id, Assert.Single(harness.Ledger.Entries).Id);
        Assert.Null(await harness.Executions.GetUnresolvedAsync(harness.User, harness.WorkerId));
        var existing = await harness.Exits.ClaimAsync(Assert.IsType<ExperimentProtectiveExitKey>(first.Key));
        Assert.Equal(ExperimentProtectiveExitClaimResult.Existing, existing);
        var storedClaim = await harness.Executions.ClaimAsync(harness.User,
            Assert.IsType<ExperimentPaperExecutionAssociation>(
                harness.Exits.GetPaperExecutionClaim(Assert.IsType<ExperimentProtectiveExitKey>(first.Key))));
        Assert.Equal(ExperimentPaperExecutionClaimResult.Existing, storedClaim.Result);
        Assert.Equal(ExperimentPaperExecutionStatus.Completed, storedClaim.Association?.Status);
        Assert.Equal(command.Payload.Id, storedClaim.Association?.ExecutionCommandId);
        var recordedExecution = Assert.Single(await harness.PaperResults.ListByCorrelationAsync(
            harness.User, storedClaim.Association!.CorrelationId));
        Assert.Equal(ExecutionOutcome.Filled, recordedExecution.Payload.Outcome);
        Assert.Equal(command.Payload.Id, recordedExecution.Payload.ExecutionCommandId);
        Assert.False(Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User)).Submitted);
        Assert.Single(harness.Adapter.Ledger);
    }

    [Fact]
    public async Task PriorUnknownPaperExecutionStillPreventsProtectiveSubmission()
    {
        var candle = Candle(Now.AddMinutes(-1), 280m, 320m, 270m);
        var harness = new Harness(new FixedCandles(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [candle]))),
            sharedPaperClaim: true);
        var priorKey = new ExperimentDecisionKey(harness.User, harness.WorkerId, 1,
            ExperimentResearchGroup.A, "platform.ema-trend-continuation", 1,
            new string('A', 64), "BTC/USD", CandleInterval.OneMinute,
            Now.AddMinutes(-3), Now.AddMinutes(-2), Now.AddMinutes(-2));
        var prior = new ExperimentPaperExecutionAssociation(
            priorKey, "unresolved-prior-order", ExperimentPaperExecutionStatus.Claimed);
        await harness.Executions.ClaimAsync(harness.User, prior);
        await harness.Executions.CompleteAsync(harness.User, prior with
        {
            Status = ExperimentPaperExecutionStatus.Unknown
        });

        var blocked = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));
        Assert.False(blocked.Submitted);
        Assert.Empty(harness.Adapter.Ledger);
        Assert.Empty(await harness.Commands.ListForUserAsync(harness.User));
        Assert.Equal(prior with { Status = ExperimentPaperExecutionStatus.Unknown },
            await harness.Executions.GetUnresolvedAsync(harness.User, harness.WorkerId));
    }

    [Fact]
    public async Task DelayedStopUsesFreshCloseWithoutRetryingTheOriginalTrigger()
    {
        var touched = Candle(Now.AddMinutes(-1), 300m, 310m, 280m);
        var latest = Candle(Now, 275m, 280m, 270m);
        var harness = new Harness(new FixedCandles(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [touched, latest]))));

        var first = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));
        var repeated = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));

        Assert.True(first.Submitted, first.Reason);
        var key = Assert.IsType<ExperimentProtectiveExitKey>(first.Key);
        Assert.Equal(touched.CloseTimeUtc, key.CandleCloseTimeUtc);
        Assert.Contains(nameof(ExperimentProtectiveExitKind.StopLoss), key.ProtectiveExitIdentity, StringComparison.Ordinal);
        Assert.Equal(275m, Assert.Single(harness.Adapter.Ledger).Price);
        Assert.Contains("already claimed", repeated.Reason, StringComparison.Ordinal);
        Assert.Single(await harness.Commands.ListForUserAsync(harness.User));
    }

    [Fact]
    public async Task StaleTriggerCannotCreateAPaperFillEvenWhenSourceClaimsItIsAvailable()
    {
        var candle = Candle(Now.AddMinutes(-2), 300m, 310m, 280m);
        var harness = new Harness(new FixedCandles(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [candle]))));

        var result = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));

        Assert.False(result.Submitted);
        Assert.Contains("fresh closed one-minute", result.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Adapter.Ledger);
        Assert.Empty(await harness.Commands.ListForUserAsync(harness.User));
    }

    [Fact]
    public async Task DelayedTargetUsesFreshCloseRatherThanClaimingTheEarlierTarget()
    {
        var touched = Candle(Now.AddMinutes(-1), 300m, 320m, 295m);
        var latest = Candle(Now, 298m, 305m, 295m);
        var harness = new Harness(new FixedCandles(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [touched, latest]))));

        var result = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));

        Assert.True(result.Submitted, result.Reason);
        var key = Assert.IsType<ExperimentProtectiveExitKey>(result.Key);
        Assert.Equal(touched.CloseTimeUtc, key.CandleCloseTimeUtc);
        Assert.Contains(nameof(ExperimentProtectiveExitKind.TakeProfit), key.ProtectiveExitIdentity, StringComparison.Ordinal);
        Assert.Equal(298m, Assert.Single(harness.Adapter.Ledger).Price);
    }

    [Fact]
    public async Task ProtectiveExitWithoutDurableWorkerPositionDoesNotClaimOrSubmit()
    {
        var candle = Candle(Now.AddMinutes(-1), 280m, 320m, 270m);
        var harness = new Harness(new FixedCandles(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [candle]))),
            missingWorker: true);

        var result = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));

        Assert.False(result.Submitted);
        Assert.Contains("matching durable worker position", result.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Adapter.Ledger);
    }

    [Theory]
    [InlineData(ExperimentCandleSeriesBlockReason.NoData)]
    [InlineData(ExperimentCandleSeriesBlockReason.Stale)]
    [InlineData(ExperimentCandleSeriesBlockReason.UnsafeCandle)]
    public async Task MissingStaleOrUnsafeCandleEvidenceNeverCloses(ExperimentCandleSeriesBlockReason reason)
    {
        var harness = new Harness(ExperimentCandleSeriesResult.Blocked(reason));
        var results = await harness.Orchestrator.EvaluateOwnerAsync(harness.User);

        Assert.False(Assert.Single(results).Submitted);
        Assert.Contains("unavailable", results[0].Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Adapter.Ledger);
    }

    [Theory]
    [InlineData(ExperimentPaperExecutionStatus.Blocked)]
    [InlineData(ExperimentPaperExecutionStatus.Unknown)]
    public async Task RejectedOrUnknownProtectiveExitClaimsNeverRetry(ExperimentPaperExecutionStatus status)
    {
        var ledger = new InMemoryExperimentProtectiveExitLedger();
        var key = new ExperimentProtectiveExitKey(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "stop", Now);
        Assert.Equal(ExperimentProtectiveExitClaimResult.Claimed, await ledger.ClaimAsync(key));
        await ledger.CompleteAsync(key, status, "terminal");

        Assert.Equal(ExperimentProtectiveExitClaimResult.Existing, await ledger.ClaimAsync(key));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.CompleteAsync(key, ExperimentPaperExecutionStatus.Blocked, "unverified"));
    }

    [Fact]
    public async Task DisabledScheduleDoesNothingAndOwnerFaultDoesNotStopOtherOwners()
    {
        var ownerOne = Guid.NewGuid();
        var ownerTwo = Guid.NewGuid();
        var evaluator = new RecordingOwnerEvaluator(ownerOne);
        using var disabled = new ProtectiveExitWorker(NullLogger<ProtectiveExitWorker>.Instance, evaluator,
            new ActiveOwners(ownerOne, ownerTwo),
            Options.Create(new ExperimentProtectiveExitWorkerOptions { Enabled = false, EnabledUserIds = { ownerOne } }),
            new FixedTimeProvider(Now));
        Assert.Equal(0, await disabled.RunTickAsync());
        Assert.Empty(evaluator.Owners);

        using var enabled = new ProtectiveExitWorker(NullLogger<ProtectiveExitWorker>.Instance, evaluator,
            new ActiveOwners(ownerOne, ownerTwo),
            Options.Create(new ExperimentProtectiveExitWorkerOptions { Enabled = true, EnabledUserIds = { ownerOne, ownerTwo } }),
            new FixedTimeProvider(Now));
        await enabled.RunTickAsync();
        Assert.Contains(ownerOne, evaluator.Owners);
        Assert.Contains(ownerTwo, evaluator.Owners);
    }

    [Fact]
    public async Task EnabledScheduleSkipsOwnersWithoutDurableTrainingActivation()
    {
        var owner = Guid.NewGuid();
        var evaluator = new RecordingOwnerEvaluator();
        using var worker = new ProtectiveExitWorker(NullLogger<ProtectiveExitWorker>.Instance, evaluator,
            new ActiveOwners(),
            Options.Create(new ExperimentProtectiveExitWorkerOptions { Enabled = true, EnabledUserIds = { owner } }),
            new FixedTimeProvider(Now));

        Assert.Equal(0, await worker.RunTickAsync());
        Assert.Empty(evaluator.Owners);
    }

    [Fact]
    public async Task BlockedProtectiveExitWarnsOperatorWithoutLoggingTheResultDetail()
    {
        var owner = Guid.NewGuid();
        var logger = new CapturingLogger();
        var evaluator = new RecordingOwnerEvaluator(blockReason: "Unsafe candle; sensitive-detail");
        using var worker = new ProtectiveExitWorker(logger, evaluator,
            new ActiveOwners(owner),
            Options.Create(new ExperimentProtectiveExitWorkerOptions { Enabled = true, EnabledUserIds = { owner } }),
            new FixedTimeProvider(Now));

        Assert.Equal(0, await worker.RunTickAsync());
        var warning = Assert.Single(logger.Messages.Where(message => message.Level == LogLevel.Warning));
        Assert.Contains("blocked results", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-detail", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPropagatesAndHostHasNoLiveOrFuturesDependencies()
    {
        var evaluator = new RecordingOwnerEvaluator();
        using var worker = new ProtectiveExitWorker(NullLogger<ProtectiveExitWorker>.Instance, evaluator,
            new ActiveOwners(),
            Options.Create(new ExperimentProtectiveExitWorkerOptions { Enabled = true, EnabledUserIds = { Guid.NewGuid() } }),
            new FixedTimeProvider(Now));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => worker.RunTickAsync(cancelled.Token));

        var references = typeof(ProtectiveExitWorker).Assembly.GetReferencedAssemblies().Select(reference => reference.Name);
        Assert.DoesNotContain(references, name => name!.Contains("Kraken", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Futures", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Exchanges", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProfitablePeakWithRsiMacdAndHigherTimeframeRolloverTriggersExit()
    {
        var position = ProfitProtectionPosition();
        var evidence = PaperTrainingAutoSelectionService.ApprovedIntervals.ToDictionary(
            interval => interval,
            ProfitProtectionSeries);

        var result = ExperimentProfitProtectionPolicy.Evaluate(position, evidence);

        Assert.True(result.ShouldExit, result.Reason);
        Assert.Equal(106m, result.ExitPrice);
        Assert.Equal(CandleInterval.FiveMinutes, result.TriggerCandle!.Interval);
    }

    [Fact]
    public void ProfitProtectionRequiresProfitAndEveryTimeframe()
    {
        var evidence = PaperTrainingAutoSelectionService.ApprovedIntervals.ToDictionary(
            interval => interval,
            ProfitProtectionSeries);
        var unprofitable = ProfitProtectionPosition() with { EntryPrice = 113m, StopLossPrice = 108m };

        Assert.False(ExperimentProfitProtectionPolicy.Evaluate(unprofitable, evidence).ShouldExit);

        evidence.Remove(CandleInterval.OneHour);
        Assert.False(ExperimentProfitProtectionPolicy.Evaluate(ProfitProtectionPosition(), evidence).ShouldExit);
    }

    [Fact]
    public async Task ConfirmedProfitRolloverClosesOnceThroughPaperPipeline()
    {
        var evidence = PaperTrainingAutoSelectionService.ApprovedIntervals.ToDictionary(
            interval => interval,
            interval => ExperimentCandleSeriesResult.Available(ProfitProtectionSeries(interval)));
        evidence[CandleInterval.OneMinute] = ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries(
                "BTC/USD",
                CandleInterval.OneMinute,
                Now,
                [Candle(Now, 106m, 107m, 105m)]));
        var harness = new Harness(new IntervalCandles(evidence), 100m, 95m, 120m);

        var first = await harness.Orchestrator.EvaluateOwnerAsync(harness.User);
        var second = await harness.Orchestrator.EvaluateOwnerAsync(harness.User);

        Assert.True(Assert.Single(first).Submitted, first[0].Reason);
        Assert.Contains(nameof(ExperimentProtectiveExitKind.MomentumReversal), first[0].Key!.ProtectiveExitIdentity, StringComparison.Ordinal);
        Assert.Contains(second, result => result.Reason.Contains("already claimed", StringComparison.Ordinal));
        var fill = Assert.Single(harness.Adapter.Ledger);
        Assert.Equal(TradeDirection.Sell, fill.Direction);
        Assert.Equal(106m, fill.Price);
    }

    [Fact]
    public async Task VersionedInvalidationNeedsProvenanceButNeverMasksTheNumericStop()
    {
        var laterClose = Now.AddMinutes(-1);
        var unpinned = new Harness(new FixedCandles(ExitHistory()),
            entryPrice: 100m, stopLossPrice: 90m, takeProfitPrice: 120m,
            strategyVersion: 4, openedAtUtc: laterClose.AddSeconds(5));
        var missing = Assert.Single(await unpinned.Orchestrator.EvaluateOwnerAsync(unpinned.User));
        Assert.False(missing.Submitted);
        Assert.Contains("attested UTC opening signal", missing.Reason, StringComparison.Ordinal);
        Assert.Empty(unpinned.Adapter.Ledger);

        var openingCandle = new Harness(new FixedCandles(ExitHistory()),
            entryPrice: 100m, stopLossPrice: 90m, takeProfitPrice: 120m,
            strategyVersion: 4, openedAtUtc: laterClose.AddSeconds(5),
            openingSignalCloseUtc: Now);
        Assert.Empty(await openingCandle.Orchestrator.EvaluateOwnerAsync(openingCandle.User));
        Assert.Empty(openingCandle.Adapter.Ledger);

        var later = new Harness(new FixedCandles(ExitHistory()),
            entryPrice: 100m, stopLossPrice: 90m, takeProfitPrice: 120m,
            strategyVersion: 4, openedAtUtc: laterClose.AddSeconds(5),
            openingSignalCloseUtc: laterClose);
        var exit = Assert.Single(await later.Orchestrator.EvaluateOwnerAsync(later.User));
        Assert.True(exit.Submitted, exit.Reason);
        Assert.Contains(nameof(ExperimentProtectiveExitKind.StrategyInvalidation),
            exit.Key!.ProtectiveExitIdentity, StringComparison.Ordinal);
        Assert.Equal(95m, Assert.Single(later.Adapter.Ledger).Price);

        var stop = new Harness(new FixedCandles(ExitHistory(low: 89m)),
            entryPrice: 100m, stopLossPrice: 90m, takeProfitPrice: 120m,
            strategyVersion: 4, strategyParameters: """{"signalInvalidationEma":10000}""",
            openedAtUtc: laterClose.AddSeconds(5));
        var protectedResult = Assert.Single(await stop.Orchestrator.EvaluateOwnerAsync(stop.User));
        Assert.True(protectedResult.Submitted, protectedResult.Reason);
        Assert.Contains(nameof(ExperimentProtectiveExitKind.StopLoss),
            protectedResult.Key!.ProtectiveExitIdentity, StringComparison.Ordinal);
        Assert.Equal(95m, Assert.Single(stop.Adapter.Ledger).Price);
    }

    [Fact]
    public async Task InvalidSavedHoldingSettingsAreReportedWithoutMakingUpAnExpiry()
    {
        var harness = new Harness(new FixedCandles(ExitHistory(lastClose: 100m)),
            entryPrice: 100m, stopLossPrice: 90m, takeProfitPrice: 120m,
            strategyParameters: """{"maximumHoldingCandles":10000}""",
            openedAtUtc: Now.AddMinutes(-1).AddSeconds(5));

        var result = Assert.Single(await harness.Orchestrator.EvaluateOwnerAsync(harness.User));

        Assert.False(result.Submitted);
        Assert.Contains("maximum-holding settings require review", result.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Adapter.Ledger);
    }

    [Fact]
    public async Task MissingVersionedSignalHistoryIsReportedAndStillAllowsAnIndependentStop()
    {
        var noSignal = new IntervalCandles(new Dictionary<CandleInterval, ExperimentCandleSeriesResult>
        {
            [CandleInterval.OneMinute] = ExitHistory(lastClose: 100m)
        });
        var blocked = new Harness(noSignal, entryPrice: 100m, stopLossPrice: 90m,
            takeProfitPrice: 120m, strategyVersion: 4,
            signalInterval: CandleInterval.FiveMinutes,
            openedAtUtc: Now.AddMinutes(-1).AddSeconds(5));
        var missing = Assert.Single(await blocked.Orchestrator.EvaluateOwnerAsync(blocked.User));
        Assert.False(missing.Submitted);
        Assert.Contains("invalidation evidence is unavailable", missing.Reason, StringComparison.Ordinal);
        Assert.Empty(blocked.Adapter.Ledger);

        var stop = new Harness(new IntervalCandles(new Dictionary<CandleInterval, ExperimentCandleSeriesResult>
        {
            [CandleInterval.OneMinute] = ExitHistory(low: 89m)
        }), entryPrice: 100m, stopLossPrice: 90m, takeProfitPrice: 120m,
            strategyVersion: 4, signalInterval: CandleInterval.FiveMinutes,
            openedAtUtc: Now.AddMinutes(-1).AddSeconds(5));
        Assert.True(Assert.Single(await stop.Orchestrator.EvaluateOwnerAsync(stop.User)).Submitted);
        Assert.Equal(95m, Assert.Single(stop.Adapter.Ledger).Price);
    }

    private static ExperimentCandleSeriesResult ExitHistory(decimal low = 94m, decimal lastClose = 95m)
    {
        var candles = Enumerable.Range(0, 60).Select(index =>
        {
            var closeTime = Now.AddMinutes(index - 59);
            return new Candle("BTC/USD", CandleInterval.OneMinute, closeTime.AddMinutes(-1), closeTime,
                100m, 101m, index == 59 ? low : 99m, index == 59 ? lastClose : 100m,
                10m, true, false);
        }).ToArray();
        return ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, candles));
    }

    private static ExperimentProtectiveExitPosition ProfitProtectionPosition() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "BTC/USD",
            1m,
            100m,
            Now.AddHours(-10),
            95m,
            120m);

    private static ExperimentCandleSeries ProfitProtectionSeries(CandleInterval interval)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var closes = Enumerable.Range(0, 32)
            .Select(index => 100m + (index * 0.25m) + (index % 2 == 0 ? 0.5m : -0.3m))
            .Concat([110m, 114m, 106m])
            .ToArray();
        var candles = closes.Select((close, index) =>
        {
            var openTime = Now.AddTicks(-duration.Ticks * (closes.Length - index));
            return new Candle(
                "BTC/USD",
                interval,
                openTime,
                openTime.Add(duration),
                close - 0.2m,
                close + (index == closes.Length - 2 ? 1m : 0.5m),
                close - 0.5m,
                close,
                100m + index,
                true,
                false);
        }).ToArray();
        return new ExperimentCandleSeries("BTC/USD", interval, Now, candles);
    }

    private static Candle Candle(DateTimeOffset close, decimal open, decimal high, decimal low) =>
        new("BTC/USD", CandleInterval.OneMinute, close.AddMinutes(-1), close, open, high, low, open, 1m, true, false);

    private sealed class Harness
    {
        public Guid User { get; } = Guid.NewGuid();
        public Guid WorkerId { get; } = Guid.NewGuid();
        public PaperExecutionAdapter Adapter { get; } = new();
        public InMemoryExecutionCommandRepository Commands { get; } = new();
        public InMemoryPaperExecutionResultRepository PaperResults { get; } = new();
        public InMemoryExperimentPaperExecutionLedger Executions { get; } = new();
        public RecordedPaperWorkerLedger Ledger { get; } = new();
        public ExperimentWorker Worker { get; }
        public IExperimentProtectiveExitLedger Exits { get; }
        public ExperimentProtectiveExitOrchestrator Orchestrator { get; }

        public Harness(Candle candle) : this(new FixedCandles(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [candle])))) { }

        public Harness(ExperimentCandleSeriesResult candles) : this(new FixedCandles(candles)) { }

        public Harness(
            IExperimentCandleSeriesSource candles,
            decimal entryPrice = 300m,
            decimal stopLossPrice = 290m,
            decimal takeProfitPrice = 310m,
            bool missingWorker = false,
            int strategyVersion = 2,
            string strategyParameters = "{}",
            DateTimeOffset? openingSignalCloseUtc = null,
            DateTimeOffset? openedAtUtc = null,
            CandleInterval signalInterval = CandleInterval.OneMinute,
            bool sharedPaperClaim = false)
        {
            var openedAt = openedAtUtc ?? Now.AddHours(-1);
            var paperWorker = new ExperimentWorker(WorkerId, User, "paper-worker",
                "platform.ema-trend-continuation", "BTC/USD", 100_000m, Now.AddHours(-2), 1);
            Worker = paperWorker;
            paperWorker.UpdateStrategyParameters(strategyParameters);
            paperWorker.Start();
            paperWorker.ApplyPaperTrade(2m, entryPrice, 0m, "buy", openedAt);
            var position = new ExperimentProtectiveExitPosition(User, WorkerId, Guid.NewGuid(), "BTC/USD", 2m, entryPrice,
                openedAt, stopLossPrice, takeProfitPrice, missingWorker ? null : paperWorker,
                signalInterval, strategyVersion, openingSignalCloseUtc);
            var decisions = new InMemoryExperimentDecisionLedger();
            var pipeline = new TradePipeline(new InMemoryMarketEventRepository(), new InMemoryStrategyDecisionRepository(),
                new InMemoryTradeIntentRepository(), new InMemoryRiskEvaluationRepository(), Commands,
                new InMemoryPortfolioUpdateRepository(), new RecordingAudit(), new RiskEngine(), new InMemoryTradingHaltState(),
                new OrderIdempotencyGuard(), new TradePipelineOptions { MaxNotional = 100_000m, MaxPositionSize = 100m },
                timeProvider: new FixedTimeProvider(Now),
                paperResults: sharedPaperClaim ? PaperResults : null);
            var paper = new PaperExperimentTradeOrchestrator(decisions, Executions, pipeline, Adapter,
                workerLedger: sharedPaperClaim ? Ledger : null,
                workers: sharedPaperClaim ? new InMemoryExperimentWorkerRepository() : null);
            Exits = sharedPaperClaim
                ? new DurablePaperTrainingProtectiveExitLedger(Executions)
                : new InMemoryExperimentProtectiveExitLedger();
            Orchestrator = new ExperimentProtectiveExitOrchestrator(
                new FixedPositions(position), candles, Exits,
                decisions, paper, new FixedTimeProvider(Now));
        }
    }

    private sealed class RecordedPaperWorkerLedger : IPaperTradingLedgerRepository
    {
        public List<PaperTradingLedgerEntry> Entries { get; } = [];

        public Task AddAsync(Guid userId, PaperTradingLedgerEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyCollection<PaperTradingLedgerEntry>> ListAsync(
            Guid userId, Guid workerId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyCollection<PaperTradingLedgerEntry>>(
                Entries.Where(entry => entry.WorkerId == workerId).ToArray());
        }
    }

    private sealed class FixedPositions(ExperimentProtectiveExitPosition position) : IExperimentProtectiveExitPositionSource
    {
        public Task<IReadOnlyList<ExperimentProtectiveExitPosition>> ListOpenAsync(Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExperimentProtectiveExitPosition>>(userId == position.UserId ? [position] : []);
    }

    private sealed class FixedCandles(ExperimentCandleSeriesResult result) : IExperimentCandleSeriesSource
    {
        public Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(ExperimentCandleSeriesRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class IntervalCandles(
        IReadOnlyDictionary<CandleInterval, ExperimentCandleSeriesResult> results) : IExperimentCandleSeriesSource
    {
        public Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
            ExperimentCandleSeriesRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(results.TryGetValue(request.Interval, out var result)
                ? result
                : ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.NoData));
    }

    private sealed class RecordingAudit : IAuditEventWriter
    {
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOwnerEvaluator(Guid? faultingOwner = null, string? blockReason = null) : IExperimentProtectiveExitOwnerEvaluator
    {
        public List<Guid> Owners { get; } = [];
        public Task<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>> EvaluateOwnerAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Owners.Add(userId);
            if (userId == faultingOwner)
                throw new InvalidOperationException("isolated");
            return Task.FromResult<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>>(
                blockReason is null ? [] : [new(null, false, blockReason)]);
        }
    }

    private sealed class CapturingLogger : ILogger<ProtectiveExitWorker>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class ActiveOwners(params Guid[] owners) : IPaperTrainingProtectionOwnerSource
    {
        public Task<IReadOnlyCollection<Guid>> GetProtectedOwnerIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Guid>>(owners);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
