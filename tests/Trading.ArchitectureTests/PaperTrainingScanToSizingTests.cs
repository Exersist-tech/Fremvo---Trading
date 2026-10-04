using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Audit;
using Trading.Domain.Experiments;
using Trading.Domain.Execution;
using Trading.Domain.Identity;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.MarketData;
using Trading.Infrastructure.Data.Experiments;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Risk;
using Trading.Workers.Experiments;
using Trading.Workers.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingScanToSizingTests
{
    [Fact]
    public async Task AdmissionRejectsConflictingAuthoritativeHistoryWithoutReplacingStoredCandle()
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        const string symbol = "COIN/EUR";
        var history = new RisingHistory(boundary);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var candles = new EfCandleRepository(db);
        var series = await history.FetchAsync(symbol, CandleInterval.FiveMinutes, boundary.AddDays(-1));
        var first = series[0];
        var conflicting = new Candle(first.Symbol, first.Interval, first.OpenTimeUtc, first.CloseTimeUtc,
            first.Open, first.High, first.Low, first.Close, first.Volume + 1m, true, false);
        Assert.Equal(CandleWriteResult.Inserted, await candles.UpsertAsync(conflicting));

        Assert.False(await new DurablePaperScanEvidenceStager(history, candles).StageAsync(
            symbol, boundary, [new ExperimentCandleSeries(symbol, CandleInterval.FiveMinutes, boundary, series)]));
        var unchanged = Assert.Single(await candles.ListAsync(
            symbol, CandleInterval.FiveMinutes, first.OpenTimeUtc, first.OpenTimeUtc));
        Assert.Equal(conflicting.Volume, unchanged.Volume);
    }

    [Fact]
    public async Task UniverseDailyStagingNeedsNoMinuteHistoryAndRejectsConflictingCandles()
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        const string symbol = "COIN/EUR";
        var daily = await new RisingHistory(boundary).FetchAsync(
            symbol, CandleInterval.OneDay, boundary.AddDays(-321));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var repository = new EfCandleRepository(db);
        var stager = new DurablePaperScanEvidenceStager(new NoExtraHistory(), repository);
        var snapshot = new ExperimentCandleSeries(symbol, CandleInterval.OneDay,
            daily[^1].CloseTimeUtc, daily);

        Assert.True(await stager.StageUniverseDailyAsync(symbol, boundary, [snapshot]));
        var first = daily[0];
        var changed = new Candle(first.Symbol, first.Interval, first.OpenTimeUtc, first.CloseTimeUtc,
            first.Open, first.High, first.Low, first.Close, first.Volume + 1m, true, false);
        Assert.False(await stager.StageUniverseDailyAsync(symbol, boundary,
        [
            new ExperimentCandleSeries(symbol, CandleInterval.OneDay, daily[^1].CloseTimeUtc,
                [changed, .. daily.Skip(1)])
        ]));
        Assert.Equal(first.Volume, Assert.Single(await repository.ListAsync(
            symbol, CandleInterval.OneDay, first.OpenTimeUtc, first.OpenTimeUtc)).Volume);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("gapped")]
    [InlineData("incomplete")]
    [InlineData("unsafe")]
    [InlineData("future")]
    [InlineData("stale")]
    [InlineData("stop-crossed")]
    [InlineData("target-crossed")]
    [InlineData("reward-eroded")]
    [InlineData("rsi-reward-eroded")]
    [InlineData("macd-reward-eroded")]
    [InlineData("ema-reward-eroded")]
    [InlineData("compression-reward-eroded")]
    [InlineData("three-swing-reward-eroded")]
    [InlineData("ensemble-reward-eroded")]
    [InlineData("invalid-ohlc")]
    [InlineData("missing-filters")]
    [InlineData("off-tick")]
    [InlineData("inactive-pair")]
    [InlineData("minimum-notional")]
    [InlineData("quantity-step")]
    public async Task LaterExecutionEvidenceMustBeSafeContiguousFreshAndWithinTheFrozenPlan(string scenario)
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(boundary);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var strategyId = scenario is "target-crossed" or "reward-eroded"
            ? "platform.donchian-breakout-ensemble"
            : scenario == "rsi-reward-eroded" ? "platform.rsi-pullback"
            : scenario == "macd-reward-eroded" ? "platform.macd-volume"
            : scenario == "compression-reward-eroded" ? "platform.volatility-compression-breakout"
            : scenario == "three-swing-reward-eroded" ? "platform.three-swing-channel-divergence"
            : scenario == "ensemble-reward-eroded" ? "platform.regime-switching-ensemble"
            : "platform.ema-trend-continuation";
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [], new(true, true, true, true, true, true),
            boundary, owner, StrategyAssignments: Enumerable.Range(1, 10)
                .Select(slot => new PaperTrainingStrategyAssignment(slot, strategyId)).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        IHistoricalCandleSource history = scenario == "rsi-reward-eroded"
            ? new RsiPullbackHistory(boundary)
            : scenario == "macd-reward-eroded" ? new MacdCrossHistory(boundary)
            : scenario == "compression-reward-eroded" ? new CompressionBreakoutHistory(boundary)
            : scenario == "three-swing-reward-eroded" ? new ThreeSwingHistory(boundary)
            : scenario == "ensemble-reward-eroded"
                ? new ContinuousPaperOpportunityScannerTests.RegimeHistorySource(boundary)
            : new RisingHistory(boundary);
        ITradablePairSource pairSource = (ITradablePairSource)history;
        if (scenario is "missing-filters" or "off-tick" or "inactive-pair"
            or "minimum-notional" or "quantity-step")
        {
            var original = Assert.Single(await pairSource.ListAsync());
            pairSource = new FixedPairSource(new TradablePair(
                original.Symbol, original.DisplayName, original.BaseAsset, original.QuoteAsset,
                scenario != "inactive-pair",
                original.MinimumQuantity,
                scenario == "quantity-step" ? .5m : original.QuantityStep,
                scenario == "off-tick" ? .3m : original.PriceTick,
                scenario == "missing-filters" ? null
                    : scenario == "minimum-notional" ? 1_000m : 10m));
        }
        var candles = new EfCandleRepository(db);
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(pairSource, history, PaperTrainingUniversePolicy.PlatformDefault),
            history, registry, activations, workers,
            new DurablePaperScanEvidenceStager(history, candles), clock,
            new InMemoryExperimentPaperExecutionLedger());
        var scan = await scanner.ScanAsync(owner);
        if (scenario is "missing-filters" or "inactive-pair")
        {
            Assert.Equal(0, scan.AdmittedCandidates);
            Assert.Empty((await activations.GetAsync(owner))!.Slots);
            return;
        }
        Assert.True(scan.AdmittedCandidates > 0);
        var interval = strategyId == "platform.regime-switching-ensemble"
            ? CandleInterval.FourHours
            : strategyId is "platform.donchian-breakout-ensemble"
            or "platform.macd-volume" or "platform.volatility-compression-breakout"
            ? CandleInterval.FifteenMinutes : CandleInterval.FiveMinutes;
        var slots = (await activations.GetAsync(owner))!.Slots;
        Assert.True(slots.Any(item => item.Interval == interval),
            $"Expected {interval}; admitted {string.Join(", ", slots.Select(item => $"{item.StrategyId}:{item.Interval}"))}");
        var slot = slots.Single(item => item.Interval == interval
            && (strategyId != "platform.regime-switching-ensemble" || item.Symbol == "ETH/EUR"));
        clock.UtcNow = boundary.AddSeconds(5);
        var configuration = (await new PaperTrainingConfigurationSource(
            workers, clock, registry, activations).GetAsync(owner, CancellationToken.None))!;
        var worker = (await workers.ListAsync(owner)).Single(item =>
            item.Name == ContinuousPaperOpportunityScanner.WorkerName(slot));
        var assignment = configuration.Assignments.Single(item => item.WorkerId == worker.Id);
        var durable = new DurableExperimentCandleSeriesSource(candles);
        var analysis = await new PaperExperimentWorkerRunner(durable, registry,
            strategyId == "platform.regime-switching-ensemble"
                ? new PlatformSupplementalExperimentEvidenceProvider(durable, activations, registry)
                : null)
            .AnalyzeAcrossPaperTimeframesAsync(worker, configuration, assignment, clock.UtcNow);
        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, analysis.Outcome);

        if (scenario != "missing")
        {
            var start = scenario == "gapped" ? boundary.AddMinutes(1) : boundary;
            var low = scenario == "stop-crossed" ? .1m : 419.3m;
            var high = scenario == "target-crossed" ? 999m
                : scenario == "invalid-ohlc" ? 419.55m : 419.7m;
            var close = scenario is "target-crossed" ? 419.4m : 419.6m;
            var successor = scenario == "rsi-reward-eroded"
                ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                    start, start.AddMinutes(1), 196m, 200.1m, 195.9m, 200m,
                    100m, true, false)
                : scenario == "macd-reward-eroded"
                ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                    start, start.AddMinutes(1), 200.6m, 203.1m, 200.5m, 203m,
                    100m, true, false)
                : scenario == "ema-reward-eroded"
                ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                    start, start.AddMinutes(1), 419.5m, 438m, 419.4m, 438m,
                    100m, true, false)
                : scenario == "compression-reward-eroded"
                ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                    start, start.AddMinutes(1), 100.3m, 100.85m, 100.2m, 100.85m,
                    100m, true, false)
                : scenario == "three-swing-reward-eroded"
                ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                    start, start.AddMinutes(1), 130m, 205m, 129.9m, 205m,
                    100m, true, false)
                : scenario == "ensemble-reward-eroded"
                ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                    start, start.AddMinutes(1), 115.95m, 117.9m, 115.9m, 117.9m,
                    100m, true, false)
                : new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                    start, start.AddMinutes(1), 419.5m, high, low, close, 100m,
                    scenario != "incomplete", false,
                    scenario == "unsafe" ? [DataQualityIssue.Stale] : null);
            Assert.Equal(CandleWriteResult.Inserted, await candles.UpsertAsync(successor));
        }
        clock.UtcNow = scenario == "future" ? boundary.AddSeconds(5)
            : scenario == "stale" ? boundary.AddMinutes(2).AddSeconds(5)
            : boundary.AddMinutes(1).AddSeconds(5);
        using var services = new ServiceCollection().AddSingleton<ICandleRepository>(candles)
            .BuildServiceProvider();
        var snapshot = await new DurablePaperTrainingSizingSnapshotSource(
            services.GetRequiredService<IServiceScopeFactory>(), clock)
            .GetAsync(worker, configuration, assignment, analysis);

        Assert.Null(snapshot);
        Assert.Equal(0m, worker.PositionQuantity);
        if (scenario == "missing")
        {
            var decisions = new InMemoryExperimentDecisionLedger();
            var executions = new InMemoryExperimentPaperExecutionLedger();
            var paper = new PaperExperimentTradeOrchestrator(decisions, executions,
                new TradePipeline(new InMemoryMarketEventRepository(), new InMemoryStrategyDecisionRepository(),
                    new InMemoryTradeIntentRepository(), new InMemoryRiskEvaluationRepository(),
                    new InMemoryExecutionCommandRepository(), new InMemoryPortfolioUpdateRepository(),
                    new InMemoryAuditEventWriter(), new RiskEngine(), new InMemoryTradingHaltState(),
                    new OrderIdempotencyGuard(), new TradePipelineOptions(), timeProvider: clock),
                new PaperExecutionAdapter(clock));
            var runner = new PaperTrainingSizedExecutionRunner(
                new PaperExperimentWorkerRunner(new DurableExperimentCandleSeriesSource(candles), registry),
                new PaperTrainingConfigurationSource(workers, clock, registry, activations),
                new DurablePaperTrainingSizingSnapshotSource(
                    services.GetRequiredService<IServiceScopeFactory>(), clock),
                new ExperimentDecisionPolicy(decisions, clock),
                new ExperimentWorkerRiskEvaluator(new RiskEngine()), paper, workers,
                new RecordingPaperLedger(), new EfExperimentPaperPlanEvidenceRepository(db),
                activations, clock, executions);
            clock.UtcNow = boundary.AddSeconds(5);
            await runner.RunOnceAsync(worker, CancellationToken.None);
            Assert.Equal(ExperimentWorkerStatus.Running, (await workers.GetAsync(owner, worker.Id))!.Status);
            clock.UtcNow = boundary.AddMinutes(6);
            await runner.RunOnceAsync(worker, CancellationToken.None);
            Assert.Equal(ExperimentWorkerStatus.Completed, (await workers.GetAsync(owner, worker.Id))!.Status);
            Assert.False(await executions.HasUnresolvedAsync(owner, worker.Id));
            Assert.Equal(0m, worker.PositionQuantity);
        }
    }

    [Fact]
    public async Task LegacyDonchianPlanSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.donchian-breakout-ensemble";
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var active = new PaperTrainingActivation(owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"shortChannel":25}""")).ToArray());
        Assert.True(await activations.TrySaveAsync(active, null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new RisingHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy Donchian protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyBollingerPlanSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.bollinger-mean-reversion";
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"bollingerPeriod":25}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new RisingHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy Bollinger protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyRsiPullbackSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.rsi-pullback";
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"longRsiMinimum":31}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new RsiPullbackHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy RSI pullback protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyMacdSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.macd-volume";
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"macdSignal":9}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new MacdCrossHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy MACD protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyEmaSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.ema-trend-continuation";
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"signalPullbackEma":20}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new RisingHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy EMA protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyCompressionSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.volatility-compression-breakout";
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"channelPeriod":20}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new CompressionBreakoutHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy compression protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterRelativeStrengthAdmissionReplaysFromDurableCandlesAndSizesOnTheConfirmedHour(
        bool entitlementExpiresBeforeSubmission)
    {
        const string family = "platform.relative-strength-pullback-rotation";
        var boundary = new DateTimeOffset(2026, 9, 20, 13, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(boundary);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations, new InMemoryAuditEventWriter(), clock);
        var prerequisites = new PaperTrainingPrerequisites(true, true, true, true, true, true);
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        await service.DisableAsync(owner, owner, RoleType.User);
        await service.ConfigureScannerStrategiesAsync(owner, owner, RoleType.User,
            Enumerable.Range(1, 10).Select(index =>
                new PaperTrainingStrategyAssignment(index, family)).ToArray());
        await service.StartScannerAsync(owner, owner, RoleType.User, prerequisites);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var candles = new EfCandleRepository(db);
        var history = new RelativeStrengthHistory(boundary);
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(history, history, PaperTrainingUniversePolicy.PlatformDefault),
            history, registry, activations, workers,
            new DurablePaperScanEvidenceStager(history, candles), clock,
            new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);
        var admitted = (await activations.GetAsync(owner))!;
        var slot = Assert.Single(admitted.Slots);
        Assert.Equal(1, scan.AdmittedCandidates);
        Assert.Equal(family, slot.StrategyId);
        Assert.Equal(5, slot.StrategyVersion);
        Assert.Equal("ETH/EUR", slot.Symbol);
        Assert.Equal(CandleInterval.FourHours, slot.Interval);
        Assert.Equal(boundary, slot.AdmissionCloseUtc);
        Assert.Equal(["ETH/EUR", "XBT/EUR"], slot.RankingUniverseSymbols);
        Assert.Contains(admitted.QualificationResults, result =>
            result.CandidateDisposition == PaperTrainingCandidateDisposition.Admitted
            && result.SignalCloseUtc == boundary);

        clock.UtcNow = boundary.AddSeconds(5);
        var configuration = await new PaperTrainingConfigurationSource(
            workers, clock, registry, activations).GetAsync(owner, CancellationToken.None);
        Assert.NotNull(configuration);
        var worker = Assert.Single(await workers.ListAsync(owner));
        var assignment = Assert.Single(configuration.Assignments);
        Assert.Equal(new PaperExchangeFilters(.0001m, .0001m, .0001m, 10m),
            assignment.Provenance.PairFilters);
        var durable = new DurableExperimentCandleSeriesSource(candles);
        var analysis = await new PaperExperimentWorkerRunner(durable, registry,
            new PlatformSupplementalExperimentEvidenceProvider(durable, activations, registry))
            .AnalyzeAsync(worker, configuration, assignment, clock.UtcNow);

        Assert.True(analysis.Outcome == ExperimentAnalysisOutcome.Analyzed, analysis.Reason);
        Assert.True(analysis.Value > 0m, analysis.Reason);
        Assert.Equal(CandleInterval.OneHour, analysis.Evidence!.Interval);
        Assert.Equal(boundary, analysis.Evidence.AsOfUtc);
        using var services = new ServiceCollection().AddSingleton<ICandleRepository>(candles)
            .BuildServiceProvider();
        var paperAdapter = new PaperExecutionAdapter(clock);
        var snapshotSource = new DurablePaperTrainingSizingSnapshotSource(
            services.GetRequiredService<IServiceScopeFactory>(), clock, paperAdapter);
        var decisions = new InMemoryExperimentDecisionLedger();
        var executions = new InMemoryExperimentPaperExecutionLedger();
        var paperLedger = new RecordingPaperLedger();
        var plans = new EfExperimentPaperPlanEvidenceRepository(db);
        var paper = new PaperExperimentTradeOrchestrator(decisions, executions,
            new TradePipeline(new EfPaperMarketEvents(db), new EfPaperStrategyDecisions(db),
                new EfPaperTradeIntents(db), new EfPaperRiskEvaluations(db),
                new EfPaperExecutionCommands(db), new EfPaperPortfolioUpdates(db),
                new InMemoryAuditEventWriter(), new RiskEngine(), new EfAuditTradingHaltState(db),
                new OrderIdempotencyGuard(), new TradePipelineOptions
                {
                    MaxNotional = 100m,
                    MaxPositionSize = 100m
                }, timeProvider: clock),
            paperAdapter, workerLedger: paperLedger, workers: workers);
        var limit = new ExpiringPaperLimit();
        var runner = new PaperTrainingSizedExecutionRunner(
            new PaperExperimentWorkerRunner(durable, registry,
                new PlatformSupplementalExperimentEvidenceProvider(durable, activations, registry)),
            new PaperTrainingConfigurationSource(workers, clock, registry, activations),
            snapshotSource,
            new ExperimentDecisionPolicy(decisions, clock),
            new ExperimentWorkerRiskEvaluator(new RiskEngine()),
            paper, workers, paperLedger, plans, activations, clock, executions,
            admissionLimit: limit);
        Assert.Null(await snapshotSource.GetAsync(worker, configuration, assignment, analysis));
        await runner.RunOnceAsync(worker, CancellationToken.None);
        Assert.Equal(ExperimentWorkerStatus.Running, (await workers.GetAsync(owner, worker.Id))!.Status);
        Assert.Empty(paperAdapter.Ledger);
        Assert.Empty(await plans.ListAsync(owner, worker.Id));
        var successor = new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
            boundary, boundary.AddMinutes(1), 189.4m, 189.6m, 189.3m, 189.45m, 100m, true, false);
        Assert.Equal(CandleWriteResult.Inserted, await candles.UpsertAsync(successor));
        clock.UtcNow = boundary.AddMinutes(1).AddSeconds(5);
        var snapshot = await snapshotSource
            .GetAsync(worker, configuration, assignment, analysis);
        Assert.NotNull(snapshot);
        Assert.Equal(boundary, snapshot.Plan.AsOfUtc);
        Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
        Assert.Equal(65, snapshot.Plan.SourceCandles.Count);
        var setupCandles = await candles.ListAsync("ETH/EUR", CandleInterval.FourHours,
            boundary.AddHours(-1 - 4 * 51), boundary.AddHours(-5));
        Assert.Equal(51, setupCandles.Count);
        var setupAtr = new Trading.Indicators.AverageTrueRangeCalculator(14).Calculate(
            setupCandles.ToArray()).Value;
        Assert.NotNull(setupAtr);
        Assert.Equal(setupCandles.TakeLast(5).Min(candle => candle.Low) - setupAtr!.Value * .2m,
            snapshot.Plan.ProtectiveStopPrice);
        Assert.Equal(setupCandles.TakeLast(20).Max(candle => candle.High),
            snapshot.Plan.ConservativeTargetPrice);
        Assert.Equal(analysis.Evidence.CloseTimeUtc, snapshot.Context.Candle.CloseTimeUtc);
        Assert.Equal(successor.Close, snapshot.Context.ExecutionCandle!.ClosePrice);
        Assert.Equal(successor.Close, snapshot.SizingInput.EntryPrice);
        Assert.Equal(paperAdapter.EstimatedTakerFeeRate, snapshot.SizingInput.EstimatedEntryFeeRate);
        Assert.Equal(.0001m, snapshot.SizingInput.ExchangeFilters!.PriceTick);
        Assert.Equal(10m, snapshot.SizingInput.ExchangeFilters.MinimumNotional);
        Assert.Equal(successor.Close, snapshot.WorkerRiskRequest.ProposedFill!.ReferencePrice);
        Assert.True(PaperRiskPositionSizer.Size(snapshot.SizingInput).IsAccepted);

        limit.ExpireAfterNextRead = entitlementExpiresBeforeSubmission;
        await runner.RunOnceAsync(worker, CancellationToken.None);
        if (entitlementExpiresBeforeSubmission)
        {
            Assert.Equal(2, limit.ReadsSinceExpiryArmed);
            Assert.Empty(paperAdapter.Ledger);
            Assert.Empty(paperLedger.Entries);
            Assert.Empty(await plans.ListAsync(owner, worker.Id));
            Assert.Equal(0m, (await workers.GetAsync(owner, worker.Id))!.PositionQuantity);
            return;
        }
        await runner.RunOnceAsync(worker, CancellationToken.None);

        Assert.Single(paperAdapter.Ledger);
        Assert.Equal(successor.Close, Assert.Single(paperAdapter.Ledger).Price);
        Assert.Single(paperLedger.Entries);
        Assert.Equal(Assert.Single(paperAdapter.Ledger).ExecutionCommandId,
            Assert.Single(paperLedger.Entries).Id);
        Assert.True((await workers.GetAsync(owner, worker.Id))!.PositionQuantity > 0m);
        Assert.Equal(boundary,
            Assert.Single(await decisions.ListAsync(owner, worker.Id)).Key.CloseTimeUtc);
        var eventRecord = Assert.Single(await new EfPaperMarketEvents(db).ListForUserAsync(owner));
        Assert.Equal(successor.Close, eventRecord.Payload.LastPrice);
        Assert.Equal(successor.CloseTimeUtc, eventRecord.Payload.EventTimeUtc);
        var recordedPlan = Assert.Single(await plans.ListAsync(owner, worker.Id));
        Assert.Equal(snapshot.Plan.ProtectiveStopPrice, recordedPlan.ProtectiveStopPrice);
        Assert.Equal(snapshot.Plan.ConservativeTargetPrice, recordedPlan.ConservativeTargetPrice);
        Assert.Equal(CandleInterval.OneHour, recordedPlan.DecisionKey.Interval);
        var position = Assert.Single(await new DurablePaperTrainingProtectiveExitPositionSource(
            workers, plans).ListOpenAsync(owner));
        Assert.Equal(CandleInterval.FourHours, position.SignalInterval);
        Assert.Equal(5, position.StrategyVersion);
        Assert.Equal(boundary, position.OpeningSignalCloseUtc);
        Assert.False(ApprovedStrategyHoldingPolicy.IsExpired(position,
            position.OpenedAtUtc.AddHours(180)));
        Assert.True(ApprovedStrategyHoldingPolicy.IsExpired(position,
            position.OpenedAtUtc.AddHours(180 * 4)));
    }

    private sealed class ExpiringPaperLimit : IPaperWorkerAdmissionLimit
    {
        public bool ExpireAfterNextRead { get; set; }
        public int ReadsSinceExpiryArmed { get; private set; }

        public Task<int> GetMaximumAsync(Guid ownerId, DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default)
        {
            if (!ExpireAfterNextRead)
                return Task.FromResult(PaperTrainingActivationService.MaximumSlots);
            ReadsSinceExpiryArmed++;
            return Task.FromResult(ReadsSinceExpiryArmed == 1
                ? PaperTrainingActivationService.MaximumSlots : 0);
        }
    }

    [Fact]
    public async Task LegacyMomentumSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.cross-sectional-momentum-rotation";
        var boundary = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"trendEma":200}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new MomentumHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy momentum protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyThreeSwingSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.three-swing-channel-divergence";
        var boundary = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"channelPeriod":50}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new ThreeSwingHistory(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy three-swing protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyEnsembleSettingsVetoNewPaperAdmissionsBeforeReservingAWorker()
    {
        const string family = "platform.regime-switching-ensemble";
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family, """{"regimeFastEma":50}""")).ToArray()), null));
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var source = new ContinuousPaperOpportunityScannerTests.RegimeHistorySource(boundary);
        var workers = new InMemoryExperimentWorkerRepository();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source, ApprovedExperimentStrategyRegistry.CreatePlatformDefault(), activations, workers,
            new DurablePaperScanEvidenceStager(source, new EfCandleRepository(db)),
            new FixedTimeProvider(boundary), new InMemoryExperimentPaperExecutionLedger());

        var scan = await scanner.ScanAsync(owner);

        Assert.Equal(0, scan.AdmittedCandidates);
        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.Contains((await activations.GetAsync(owner))!.QualificationResults, item =>
            item.Reason.Contains("Legacy ensemble protection settings need owner review",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedMomentumUniverseProducesOnlyAProtectedSafePaperFill(bool rewardEroded)
    {
        const string family = "platform.cross-sectional-momentum-rotation";
        var boundary = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(boundary);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        Assert.True(await activations.TrySaveAsync(new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10).Select(slot =>
                new PaperTrainingStrategyAssignment(slot, family)).ToArray()), null));
        var database = Guid.NewGuid().ToString("N");
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(database).Options);
        var candles = new EfCandleRepository(db);
        var history = new MomentumHistory(boundary);
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var workers = new InMemoryExperimentWorkerRepository();
        var executions = new InMemoryExperimentPaperExecutionLedger();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(history, history, PaperTrainingUniversePolicy.PlatformDefault),
            history, registry, activations, workers,
            new DurablePaperScanEvidenceStager(history, candles), clock, executions);
        var scan = await scanner.ScanAsync(owner);
        var slot = Assert.Single((await activations.GetAsync(owner))!.Slots);
        Assert.Equal(1, scan.AdmittedCandidates);
        Assert.Equal(family, slot.StrategyId);
        Assert.Equal("XBT/EUR", slot.Symbol);
        Assert.Equal(4, slot.StrategyVersion);
        Assert.Equal(CandleInterval.OneDay, slot.Interval);
        Assert.Equal(boundary, slot.AdmissionCloseUtc);
        Assert.Equal(["ETH/EUR", "XBT/EUR"], slot.RankingUniverseSymbols);

        clock.UtcNow = boundary.AddSeconds(5);
        var source = new PaperTrainingConfigurationSource(workers, clock, registry, activations);
        var configuration = (await source.GetAsync(owner, CancellationToken.None))!;
        var worker = Assert.Single(await workers.ListAsync(owner));
        var assignment = Assert.Single(configuration.Assignments);
        var durable = new DurableExperimentCandleSeriesSource(candles);
        var supplemental = new PlatformSupplementalExperimentEvidenceProvider(durable, activations, registry);
        var primary = (await durable.GetClosedSeriesAsync(new ExperimentCandleSeriesRequest(
            worker.MarketSymbol, CandleInterval.OneDay, boundary,
            ApprovedConsensusStrategyProfiles.RequiredHistory))).Series!;
        var pinned = assignment.Provenance;
        var missingUniverse = new ExperimentResearchProvenance(
            pinned.Approval, pinned.ParametersFingerprint, pinned.Dataset,
            pinned.Classifier, pinned.EvidenceProvenance, pinned.GateEvaluation,
            pinned.SelectedComponent, rankingUniverseSymbols: null,
            pinned.AdmissionCloseUtc, pinned.PairFilters);
        var blocked = await supplemental.EvaluateAsync(owner, family, primary, missingUniverse,
            worker.StrategyParameters);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, blocked!.Outcome);
        Assert.Contains("pinned eligible-universe snapshot", blocked.Reason, StringComparison.Ordinal);
        var observationRunner = new PaperExperimentWorkerRunner(durable, registry, supplemental);
        var analysis = await observationRunner.AnalyzeAsync(worker, configuration, assignment, clock.UtcNow);
        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, analysis.Outcome);
        Assert.True(analysis.Value > 0m, analysis.Reason);
        Assert.Equal(boundary, analysis.Evidence!.AsOfUtc);
        using var services = new ServiceCollection().AddSingleton<ICandleRepository>(candles)
            .BuildServiceProvider();
        var snapshotSource = new DurablePaperTrainingSizingSnapshotSource(
            services.GetRequiredService<IServiceScopeFactory>(), clock);
        Assert.Null(await snapshotSource.GetAsync(worker, configuration, assignment, analysis));
        var successor = rewardEroded
            ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 195.7m, 202.6m, 195.6m,
                202.6m, 100m, true, false)
            : new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 195.7m, 195.9m, 195.6m,
                195.8m, 100m, true, false);
        Assert.Equal(CandleWriteResult.Inserted, await candles.UpsertAsync(successor));
        clock.UtcNow = boundary.AddMinutes(1).AddSeconds(5);
        var snapshot = await snapshotSource.GetAsync(worker, configuration, assignment, analysis);
        if (rewardEroded)
        {
            Assert.Null(snapshot);
            Assert.Equal(0m, worker.PositionQuantity);
            return;
        }

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
        Assert.Equal(320, snapshot.Plan.SourceCandles.Count);
        Assert.Equal(CandleInterval.OneDay, snapshot.Context.Candle.Interval);
        Assert.Equal(successor.Close, snapshot.SizingInput.EntryPrice);
        var daily = (await candles.ListAsync(worker.MarketSymbol, CandleInterval.OneDay,
            boundary.AddDays(-320), boundary.AddDays(-1))).ToArray();
        var parameters = ApprovedStrategyParameters.Read(family, worker.StrategyParameters);
        var atr = new Trading.Indicators.AverageTrueRangeCalculator(parameters.Int32("atrPeriod"))
            .Calculate(daily.ToArray()).Value!.Value;
        var stop = daily.SkipLast(1).TakeLast(parameters.Int32("stopSwingLookback"))
            .Min(candle => candle.Low) - atr * parameters.Decimal("stopBufferAtr");
        Assert.Equal(stop, snapshot.Plan.ProtectiveStopPrice);
        Assert.Equal(daily[^1].Close + (daily[^1].Close - stop)
            * parameters.Decimal("targetRiskMultiple"), snapshot.Plan.ConservativeTargetPrice);

        var decisions = new InMemoryExperimentDecisionLedger();
        var paperLedger = new RecordingPaperLedger();
        var adapter = new PaperExecutionAdapter(clock);
        var plans = new EfExperimentPaperPlanEvidenceRepository(db);
        var paper = new PaperExperimentTradeOrchestrator(decisions, executions,
            new TradePipeline(new EfPaperMarketEvents(db), new EfPaperStrategyDecisions(db),
                new EfPaperTradeIntents(db), new EfPaperRiskEvaluations(db),
                new EfPaperExecutionCommands(db), new EfPaperPortfolioUpdates(db),
                new InMemoryAuditEventWriter(), new RiskEngine(), new EfAuditTradingHaltState(db),
                new OrderIdempotencyGuard(), new TradePipelineOptions
                {
                    MaxNotional = 100m,
                    MaxPositionSize = 100m
                }, timeProvider: clock),
            adapter, workerLedger: paperLedger, workers: workers);
        var runner = new PaperTrainingSizedExecutionRunner(observationRunner, source,
            snapshotSource, new ExperimentDecisionPolicy(decisions, clock),
            new ExperimentWorkerRiskEvaluator(new RiskEngine()), paper, workers,
            paperLedger, plans, activations, clock, executions);
        await runner.RunOnceAsync(worker, CancellationToken.None);
        await runner.RunOnceAsync(worker, CancellationToken.None);
        Assert.Single(adapter.Ledger);
        Assert.Equal(successor.Close, Assert.Single(adapter.Ledger).Price);
        Assert.True((await workers.GetAsync(owner, worker.Id))!.PositionQuantity > 0m);
        var recorded = Assert.Single(await plans.ListAsync(owner, worker.Id));
        Assert.Equal(stop, recorded.ProtectiveStopPrice);
        Assert.Equal(snapshot.Plan.ConservativeTargetPrice, recorded.ConservativeTargetPrice);
        var positions = new DurablePaperTrainingProtectiveExitPositionSource(workers, plans);
        var position = Assert.Single(await positions.ListOpenAsync(owner));
        Assert.Equal(4, position.StrategyVersion);
        Assert.Equal(boundary, position.OpeningSignalCloseUtc);
        var openedWorker = (await workers.GetAsync(owner, worker.Id))!;
        var persistedWorker = new EfExperimentWorkerRepository(db);
        await persistedWorker.SaveAsync(openedWorker);
        await persistedWorker.AddAsync(owner, Assert.Single(openedWorker.Ledger));
        await using (var reopened = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(database).Options))
        {
            var recovered = Assert.Single(await new DurablePaperTrainingProtectiveExitPositionSource(
                new EfExperimentWorkerRepository(reopened),
                new EfExperimentPaperPlanEvidenceRepository(reopened)).ListOpenAsync(owner));
            Assert.Equal(stop, recovered.StopLossPrice);
            Assert.Equal(4, recovered.StrategyVersion);
            Assert.Equal(["ETH/EUR", "XBT/EUR"], assignment.Provenance.RankingUniverseSymbols);
        }
        clock.UtcNow = boundary.AddMinutes(2).AddSeconds(5);
        Assert.Equal(CandleWriteResult.Inserted, await candles.UpsertAsync(new Candle(
            worker.MarketSymbol, CandleInterval.OneMinute,
            boundary.AddMinutes(1), boundary.AddMinutes(2),
            successor.Close, successor.Close + .1m, stop - 1m, stop,
            100m, true, false)));
        var exits = new ExperimentProtectiveExitOrchestrator(
            positions, durable, new InMemoryExperimentProtectiveExitLedger(),
            decisions, paper, clock);
        var closed = await exits.EvaluateOwnerAsync(owner);
        Assert.True(Assert.Single(closed).Submitted, closed[0].Reason);
        Assert.Equal(0m, (await workers.GetAsync(owner, worker.Id))!.PositionQuantity);
        Assert.Equal(2, adapter.Ledger.Count);
    }

    private sealed class NoExtraHistory : IHistoricalCandleSource
    {
        public Task<IReadOnlyList<Candle>> FetchAsync(string symbol, CandleInterval interval,
            DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Daily-universe staging must not request one-minute history.");
    }

    private sealed class FixedPairSource(TradablePair pair) : ITradablePairSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TradablePair>>([pair]);
        }
    }

    [Fact]
    public async Task ProtectiveExitCannotClaimAnotherCandleUntilPriorOutcomeIsResolved()
    {
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var position = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var executions = new InMemoryExperimentPaperExecutionLedger();
        var exits = new DurablePaperTrainingProtectiveExitLedger(executions);
        var first = new ExperimentProtectiveExitKey(owner, worker, position, "stop-loss", at);
        var later = first with { CandleCloseTimeUtc = at.AddMinutes(1) };

        Assert.Equal(ExperimentProtectiveExitClaimResult.Claimed, await exits.ClaimAsync(first));
        Assert.Equal(ExperimentProtectiveExitClaimResult.Existing, await exits.ClaimAsync(later));
        await exits.CompleteAsync(first, ExperimentPaperExecutionStatus.Unknown, "requires reconciliation");
        Assert.Equal(ExperimentProtectiveExitClaimResult.Existing, await exits.ClaimAsync(later));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exits.CompleteAsync(first, ExperimentPaperExecutionStatus.Blocked, "unverified no fill"));
        Assert.Equal(ExperimentProtectiveExitClaimResult.Existing, await exits.ClaimAsync(later));
    }

    [Theory]
    [InlineData("running", "platform.ema-trend-continuation")]
    [InlineData("paused", "platform.ema-trend-continuation")]
    [InlineData("failed", "platform.ema-trend-continuation")]
    [InlineData("running", "platform.donchian-breakout-ensemble")]
    [InlineData("running", "platform.bollinger-mean-reversion")]
    [InlineData("running", "platform.rsi-pullback")]
    [InlineData("running", "platform.macd-volume")]
    [InlineData("running", "platform.volatility-compression-breakout")]
    [InlineData("running", "platform.three-swing-channel-divergence")]
    [InlineData("running", "platform.regime-switching-ensemble")]
    public async Task AdmittedClosedEvidenceReachesAnExactSizedPaperPlan(
        string workerState, string strategyId)
    {
        var boundary = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(boundary);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var activation = new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), boundary, owner,
            StrategyAssignments: Enumerable.Range(1, 10)
                .Select(slot => new PaperTrainingStrategyAssignment(
                    slot, strategyId)).ToArray());
        Assert.True(await activations.TrySaveAsync(activation, null));
        IHistoricalCandleSource history = strategyId switch
        {
            "platform.bollinger-mean-reversion" => new BollingerHistory(boundary),
            "platform.rsi-pullback" => new RsiPullbackHistory(boundary),
            "platform.macd-volume" => new MacdCrossHistory(boundary),
            "platform.volatility-compression-breakout" => new CompressionBreakoutHistory(boundary),
            "platform.three-swing-channel-divergence" => new ThreeSwingHistory(boundary),
            "platform.regime-switching-ensemble" =>
                new ContinuousPaperOpportunityScannerTests.RegimeHistorySource(boundary),
            _ => new RisingHistory(boundary)
        };
        var database = Guid.NewGuid().ToString("N");
        await using var db = new TradingDbContext(
            new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase(database).Options);
        var candles = new EfCandleRepository(db);
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var workers = new InMemoryExperimentWorkerRepository();
        var executions = new InMemoryExperimentPaperExecutionLedger();
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(
                (ITradablePairSource)history, history, PaperTrainingUniversePolicy.PlatformDefault),
            history, registry, activations, workers,
            new DurablePaperScanEvidenceStager(history, candles), clock,
            executions);

        var scan = await scanner.ScanAsync(owner);
        Assert.True(scan.AdmittedCandidates > 0);
        clock.UtcNow = boundary.AddSeconds(5);
        var source = new PaperTrainingConfigurationSource(workers, clock, registry, activations);
        var configuration = await source.GetAsync(owner, CancellationToken.None);
        Assert.NotNull(configuration);
        var slot = (await activations.GetAsync(owner))!.Slots
            .Single(item => item.Interval == (strategyId == "platform.regime-switching-ensemble"
                ? CandleInterval.FourHours : strategyId is "platform.donchian-breakout-ensemble"
                or "platform.bollinger-mean-reversion" or "platform.macd-volume"
                or "platform.volatility-compression-breakout"
                ? CandleInterval.FifteenMinutes : CandleInterval.FiveMinutes)
                && (strategyId != "platform.regime-switching-ensemble" || item.Symbol == "ETH/EUR"));
        var worker = (await workers.ListAsync(owner)).Single(item =>
            item.Name == ContinuousPaperOpportunityScanner.WorkerName(slot));
        var assignment = configuration.Assignments.Single(item => item.WorkerId == worker.Id);
        Assert.Equal(slot.PairFilters, assignment.Provenance.PairFilters);
        var durable = new DurableExperimentCandleSeriesSource(candles);
        PaperExperimentWorkerRunner CreateRunner() => new(durable, registry,
            strategyId == "platform.regime-switching-ensemble"
                ? new PlatformSupplementalExperimentEvidenceProvider(durable, activations, registry)
                : null);
        var analysis = await CreateRunner()
            .AnalyzeAcrossPaperTimeframesAsync(worker, configuration, assignment, clock.UtcNow);
        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, analysis.Outcome);
        Assert.NotNull(analysis.Evidence);
        Assert.True(analysis.Value > 0m, analysis.Reason);

        using var services = new ServiceCollection()
            .AddSingleton<ICandleRepository>(candles)
            .BuildServiceProvider();
        var snapshotSource = new DurablePaperTrainingSizingSnapshotSource(
            services.GetRequiredService<IServiceScopeFactory>(), clock);
        Assert.Null(await snapshotSource.GetAsync(worker, configuration, assignment, analysis));
        var successor = strategyId == "platform.rsi-pullback"
            ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 196m, 196.2m, 195.9m, 196.1m, 100m, true, false)
            : strategyId == "platform.macd-volume"
            ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 200.6m, 200.8m, 200.5m, 200.7m, 100m, true, false)
            : strategyId == "platform.volatility-compression-breakout"
            ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 100.3m, 100.42m, 100.25m, 100.35m, 100m, true, false)
            : strategyId == "platform.three-swing-channel-divergence"
            ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 130m, 130.2m, 129.9m, 130.1m, 100m, true, false)
            : strategyId == "platform.regime-switching-ensemble"
            ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 115.95m, 116.15m, 115.9m,
                116.05m, 100m, true, false)
            : strategyId == "platform.bollinger-mean-reversion"
            ? new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 95.5m, 95.7m, 95.4m, 95.6m, 100m, true, false)
            : new Candle(worker.MarketSymbol, CandleInterval.OneMinute,
                boundary, boundary.AddMinutes(1), 419.5m, 419.7m, 419.3m,
                strategyId == "platform.donchian-breakout-ensemble" ? 419.4m : 419.6m,
                100m, true, false);
        Assert.Equal(CandleWriteResult.Inserted, await candles.UpsertAsync(successor));
        clock.UtcNow = boundary.AddMinutes(1).AddSeconds(5);
        var snapshot = await snapshotSource
            .GetAsync(worker, configuration, assignment, analysis);

        Assert.NotNull(snapshot);
        Assert.Equal(analysis.Evidence.AsOfUtc, snapshot.Plan.AsOfUtc);
        Assert.Equal(snapshot.Context.Candle.ClosePrice, snapshot.Plan.EntryReferencePrice);
        Assert.Equal(successor.Close, snapshot.SizingInput.EntryPrice);
        Assert.Equal(successor.CloseTimeUtc, snapshot.WorkerRiskRequest.ProposedFill!.OccurredAtUtc);
        Assert.True(snapshot.Plan.ProtectiveStopPrice < snapshot.Plan.EntryReferencePrice);
        if (strategyId == "platform.donchian-breakout-ensemble")
        {
            var parameters = ApprovedStrategyParameters.Read(strategyId, worker.StrategyParameters);
            var duration = TimeSpan.FromMinutes((int)slot.Interval);
            var count = Math.Max(parameters.Int32("shortChannel"), parameters.Int32("atrPeriod")) + 1;
            var signalHistory = await candles.ListAsync(worker.MarketSymbol, slot.Interval,
                boundary.AddTicks(-duration.Ticks * count), boundary.AddTicks(-duration.Ticks));
            Assert.Equal(count, signalHistory.Count);
            var atr = new Trading.Indicators.AverageTrueRangeCalculator(parameters.Int32("atrPeriod"))
                .Calculate(signalHistory.ToArray()).Value;
            Assert.NotNull(atr);
            var expectedStop = signalHistory.Take(count - 1).TakeLast(parameters.Int32("shortChannel"))
                .Min(item => item.Low) - atr!.Value * parameters.Decimal("stopBufferAtr");
            Assert.Equal(expectedStop, snapshot.Plan.ProtectiveStopPrice);
            Assert.Equal(snapshot.Plan.EntryReferencePrice
                + (snapshot.Plan.EntryReferencePrice - expectedStop) * parameters.Decimal("targetRiskMultiple"),
                snapshot.Plan.ConservativeTargetPrice);
            Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
        }
        if (strategyId == "platform.bollinger-mean-reversion")
        {
            Assert.Equal(5, slot.StrategyVersion);
            var parameters = ApprovedStrategyParameters.Read(strategyId, worker.StrategyParameters);
            var signalHistory = await candles.ListAsync(worker.MarketSymbol, slot.Interval,
                boundary.AddMinutes(-315), boundary.AddMinutes(-15));
            Assert.Equal(21, signalHistory.Count);
            var atr = new Trading.Indicators.AverageTrueRangeCalculator(parameters.Int32("atrPeriod"))
                .Calculate(signalHistory.ToArray()).Value;
            Assert.NotNull(atr);
            Assert.Equal(signalHistory.TakeLast(2).Min(item => item.Low)
                - atr!.Value * parameters.Decimal("stopBufferAtr"), snapshot.Plan.ProtectiveStopPrice);
            Assert.Equal(signalHistory.TakeLast(parameters.Int32("bollingerPeriod"))
                .Average(item => item.Close), snapshot.Plan.ConservativeTargetPrice);
            Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
        }
        if (strategyId is "platform.rsi-pullback" or "platform.macd-volume")
        {
            Assert.Equal(5, slot.StrategyVersion);
            var parameters = ApprovedStrategyParameters.Read(strategyId, worker.StrategyParameters);
            var duration = TimeSpan.FromMinutes((int)slot.Interval);
            var signalHistory = await candles.ListAsync(worker.MarketSymbol, slot.Interval,
                boundary.AddTicks(-duration.Ticks * ApprovedConsensusStrategyProfiles.RequiredHistory),
                boundary.AddTicks(-duration.Ticks));
            Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, signalHistory.Count);
            var atr = new Trading.Indicators.AverageTrueRangeCalculator(parameters.Int32("atrPeriod"))
                .Calculate(signalHistory.ToArray()).Value;
            Assert.NotNull(atr);
            var expectedStop = signalHistory.TakeLast(parameters.Int32("stopSwingLookback"))
                .Min(item => item.Low) - atr!.Value * parameters.Decimal("stopBufferAtr");
            Assert.Equal(expectedStop, snapshot.Plan.ProtectiveStopPrice);
            Assert.Equal(snapshot.Plan.EntryReferencePrice
                + (snapshot.Plan.EntryReferencePrice - expectedStop) * parameters.Decimal("targetRiskMultiple"),
                snapshot.Plan.ConservativeTargetPrice);
            Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
        }
        if (strategyId is "platform.ema-trend-continuation" or "platform.volatility-compression-breakout")
        {
            Assert.Equal(5, slot.StrategyVersion);
            var parameters = ApprovedStrategyParameters.Read(strategyId, worker.StrategyParameters);
            var duration = TimeSpan.FromMinutes((int)slot.Interval);
            var signalHistory = await candles.ListAsync(worker.MarketSymbol, slot.Interval,
                boundary.AddTicks(-duration.Ticks * ApprovedConsensusStrategyProfiles.RequiredHistory),
                boundary.AddTicks(-duration.Ticks));
            Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, signalHistory.Count);
            var atr = new Trading.Indicators.AverageTrueRangeCalculator(parameters.Int32("atrPeriod"))
                .Calculate(signalHistory.ToArray()).Value;
            Assert.NotNull(atr);
            var expectedStop = strategyId == "platform.ema-trend-continuation"
                ? signalHistory.TakeLast(parameters.Int32("stopSwingLookback"))
                    .Min(item => item.Low) - atr!.Value * parameters.Decimal("stopBufferAtr")
                : signalHistory.SkipLast(1).TakeLast(parameters.Int32("channelPeriod"))
                    .Min(item => item.Low) - atr!.Value * parameters.Decimal("stopBufferAtr");
            Assert.Equal(expectedStop, snapshot.Plan.ProtectiveStopPrice);
            Assert.Equal(snapshot.Plan.EntryReferencePrice
                + (snapshot.Plan.EntryReferencePrice - expectedStop) * parameters.Decimal("targetRiskMultiple"),
                snapshot.Plan.ConservativeTargetPrice);
            Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
        }
        if (strategyId == "platform.three-swing-channel-divergence")
        {
            Assert.Equal(4, slot.StrategyVersion);
            var parameters = ApprovedStrategyParameters.Read(strategyId, worker.StrategyParameters);
            var (signal, hourly, fourHourly) = ThreeSwingChannelDivergenceModelTests.BullishEvidence();
            var pivot = Trading.Strategies.ThreeSwingChannelDivergenceModel.Evaluate(
                signal, hourly, fourHourly).Pivots[^1];
            var atr = new Trading.Indicators.AverageTrueRangeCalculator(parameters.Int32("atrPeriod"))
                .Calculate(signal).Value!.Value;
            var expectedStop = signal.Skip(pivot.CandleIndex).Min(candle => candle.Low)
                - atr * parameters.Decimal("stopBufferAtr");
            Assert.Equal(expectedStop, snapshot.Plan.ProtectiveStopPrice);
            Assert.Equal(signal[^1].Close + (signal[^1].Close - expectedStop)
                * parameters.Decimal("targetRiskMultiple"), snapshot.Plan.ConservativeTargetPrice);
            Assert.Equal(960, snapshot.Plan.SourceCandles.Count);
            Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
        }
        if (strategyId == "platform.regime-switching-ensemble")
        {
            Assert.Equal(5, slot.StrategyVersion);
            var selected = Assert.IsType<PaperRegimeComponentSelection>(slot.SelectedComponent);
            Assert.Equal("platform.ema-trend-continuation", selected.FamilyId);
            Assert.Equal(4, selected.Version);
            var parameters = ApprovedStrategyParameters.Read(strategyId, worker.StrategyParameters);
            var signal = (await candles.ListAsync(worker.MarketSymbol, CandleInterval.FourHours,
                boundary.AddHours(-4 * 320), boundary.AddHours(-4))).ToArray();
            var atr = new Trading.Indicators.AverageTrueRangeCalculator(parameters.Int32("planAtrPeriod"))
                .Calculate(signal).Value!.Value;
            var expectedStop = signal.SkipLast(1).TakeLast(parameters.Int32("stopSwingLookback"))
                .Min(candle => candle.Low) - atr * parameters.Decimal("stopBufferAtr");
            Assert.Equal(expectedStop, snapshot.Plan.ProtectiveStopPrice);
            Assert.Equal(signal[^1].Close + (signal[^1].Close - expectedStop)
                * parameters.Decimal("targetRiskMultiple"), snapshot.Plan.ConservativeTargetPrice);
            Assert.Equal(2, snapshot.Plan.Provenance.PlanProfileVersion);
            Assert.Contains(selected.ComponentDecisionFingerprint, snapshot.Plan.Rationale,
                StringComparison.Ordinal);
        }
        Assert.True(PaperRiskPositionSizer.Size(snapshot.SizingInput).IsAccepted);
        Assert.Empty(await workers.ListAsync(Guid.NewGuid()));

        var decisions = new InMemoryExperimentDecisionLedger();
        var observationRunner = new PaperTrainingObservationRunner(
            CreateRunner(),
            source, new ExperimentDecisionPolicy(decisions, clock), clock);
        await observationRunner.RunOnceAsync(worker, CancellationToken.None);
        await observationRunner.RunOnceAsync(worker, CancellationToken.None);
        var observed = Assert.Single(await decisions.ListAsync(owner, worker.Id));
        Assert.Equal(boundary, observed.Key.AsOfUtc);
        var audit = new InMemoryAuditEventWriter();
        var events = new EfPaperMarketEvents(db);
        var pipeline = new TradePipeline(
            events,
            new EfPaperStrategyDecisions(db),
            new EfPaperTradeIntents(db),
            new EfPaperRiskEvaluations(db),
            new EfPaperExecutionCommands(db),
            new EfPaperPortfolioUpdates(db),
            audit, new RiskEngine(), new EfAuditTradingHaltState(db),
            new OrderIdempotencyGuard(),
            new TradePipelineOptions { MaxNotional = 100m, MaxPositionSize = 100m },
            timeProvider: clock);
        var adapter = new PaperExecutionAdapter(clock);
        var ledger = new RecordingPaperLedger();
        var paper = new PaperExperimentTradeOrchestrator(decisions,
            executions, pipeline, adapter,
            workerLedger: ledger, workers: workers);
        var plans = new EfExperimentPaperPlanEvidenceRepository(db);
        var runner = new PaperTrainingSizedExecutionRunner(
            CreateRunner(),
            source,
            new DurablePaperTrainingSizingSnapshotSource(
                services.GetRequiredService<IServiceScopeFactory>(), clock),
            new ExperimentDecisionPolicy(decisions, clock),
            new ExperimentWorkerRiskEvaluator(new RiskEngine()),
            paper, workers, ledger,
            plans,
            activations, clock, executions);

        await runner.RunOnceAsync(worker, CancellationToken.None);
        Assert.Single(adapter.Ledger);
        Assert.Equal(successor.Close, Assert.Single(adapter.Ledger).Price);
        Assert.Single(ledger.Entries);
        Assert.True((await workers.GetAsync(owner, worker.Id))!.PositionQuantity > 0m);
        Assert.Contains(audit.Events, item => item.Action == "Trade.PaperExecuted");
        Assert.NotEmpty(await plans.ListAsync(owner, worker.Id));
        await runner.RunOnceAsync(worker, CancellationToken.None);
        var unprotectedRunner = new PaperTrainingSizedExecutionRunner(
            CreateRunner(),
            new UnexpectedConfigurationSource(),
            new DurablePaperTrainingSizingSnapshotSource(
                services.GetRequiredService<IServiceScopeFactory>(), clock),
            new ExperimentDecisionPolicy(decisions, clock),
            new ExperimentWorkerRiskEvaluator(new RiskEngine()),
            paper, workers, ledger, new MissingPaperPlans(),
            activations, clock, executions);
        await unprotectedRunner.RunOnceAsync(worker, CancellationToken.None);
        Assert.Single(adapter.Ledger);
        var positions = new DurablePaperTrainingProtectiveExitPositionSource(workers, plans);
        var protectedPosition = Assert.Single(await positions.ListOpenAsync(owner));
        Assert.Equal(strategyId is "platform.donchian-breakout-ensemble"
            or "platform.bollinger-mean-reversion" or "platform.rsi-pullback"
            or "platform.macd-volume" or "platform.ema-trend-continuation"
            or "platform.volatility-compression-breakout"
            or "platform.regime-switching-ensemble" ? 5 : 4,
            protectedPosition.StrategyVersion);
        Assert.Equal(boundary, protectedPosition.OpeningSignalCloseUtc);
        var openedWorker = (await workers.GetAsync(owner, worker.Id))!;
        var persistedWorker = new EfExperimentWorkerRepository(db);
        await persistedWorker.SaveAsync(openedWorker);
        await persistedWorker.AddAsync(owner, Assert.Single(openedWorker.Ledger));
        await using (var reopenedDb = new TradingDbContext(
            new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(database).Options))
        {
            var reopenedPosition = Assert.Single(await new DurablePaperTrainingProtectiveExitPositionSource(
                new EfExperimentWorkerRepository(reopenedDb),
                new EfExperimentPaperPlanEvidenceRepository(reopenedDb)).ListOpenAsync(owner));
            Assert.Equal(protectedPosition.PositionId, reopenedPosition.PositionId);
            Assert.Equal(protectedPosition.StrategyVersion, reopenedPosition.StrategyVersion);
            Assert.Equal(protectedPosition.OpeningSignalCloseUtc, reopenedPosition.OpeningSignalCloseUtc);
            Assert.Equal(protectedPosition.Worker!.StrategyParameters, reopenedPosition.Worker!.StrategyParameters);
            Assert.Throws<InvalidOperationException>(() =>
                reopenedPosition.Worker.UpdateStrategyParameters("""{"signalInvalidationEma":30}"""));
        }
        if (workerState == "paused")
            openedWorker.Pause();
        if (workerState == "failed")
            openedWorker.Fail("Entry analysis failed after filling.");
        if (workerState != "running")
        {
            await workers.SaveAsync(openedWorker);
            Assert.NotEmpty(await positions.ListOpenAsync(owner));
        }
        db.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(), owner, "Trading.EmergencyStopEngaged", "TradingHalt",
            "platform", clock.UtcNow, null, "Synthetic emergency stop.", "test-halt"));
        await db.SaveChangesAsync();

        Assert.True(await activations.TrySaveAsync(
            (await activations.GetAsync(owner))! with
            { State = PaperTrainingActivationState.Disabled },
            PaperTrainingActivationState.Active));
        clock.UtcNow = boundary.AddMinutes(2).AddSeconds(5);
        var stop = snapshot.Plan.ProtectiveStopPrice;
        Assert.Equal(CandleWriteResult.Inserted, await candles.UpsertAsync(new Candle(
            worker.MarketSymbol, CandleInterval.OneMinute,
            boundary.AddMinutes(1), boundary.AddMinutes(2),
            successor.Close,
            successor.Close + .1m,
            stop - 1m, stop,
            100m, true, false)));
        var exits = new ExperimentProtectiveExitOrchestrator(
            positions,
            new DurableExperimentCandleSeriesSource(candles),
            new InMemoryExperimentProtectiveExitLedger(),
            decisions, paper, clock);
        var closed = await exits.EvaluateOwnerAsync(owner);

        Assert.Equal(2, adapter.Ledger.Count);
        Assert.True(Assert.Single(closed).Submitted, closed[0].Reason);
        Assert.Equal(2, ledger.Entries.Count);
        Assert.Equal(0m, (await workers.GetAsync(owner, worker.Id))!.PositionQuantity);
        Assert.Equal(workerState == "failed" ? ExperimentWorkerStatus.Failed : ExperimentWorkerStatus.Completed,
            (await workers.GetAsync(owner, worker.Id))!.Status);
        Assert.Contains(await workers.ListClosedAsync(owner, 0, 10), item => item.Id == worker.Id);
        Assert.True(await activations.TrySaveAsync(
            (await activations.GetAsync(owner))! with { State = PaperTrainingActivationState.Active },
            PaperTrainingActivationState.Disabled));
        clock.UtcNow = boundary.AddMinutes(5).AddSeconds(5);
        await scanner.ScanAsync(owner);
        Assert.DoesNotContain((await activations.GetAsync(owner))!.Slots,
            item => item.ProvenanceId == slot.ProvenanceId);
        await using (var restartedDb = new TradingDbContext(
            new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(database).Options))
        {
            var durableEvents = new EfPaperPipelineRecordRepository<MarketEvent>(restartedDb, PipelineStage.MarketEvent);
            var saved = await durableEvents.ListForUserAsync(owner);
            Assert.Equal(2, saved.Count);
            var opening = Assert.Single(saved, item => item.Payload.LastPrice == successor.Close);
            Assert.Equal(successor.CloseTimeUtc, opening.Payload.EventTimeUtc);
            foreach (var item in saved)
            {
                var restored = await durableEvents.GetAsync(owner, item.Id);
                Assert.NotNull(restored);
                Assert.Equal(item.Context.CorrelationId, restored.Context.CorrelationId);
                Assert.Equal(item.Payload.LastPrice, restored.Payload.LastPrice);
                Assert.Single(await durableEvents.ListByCorrelationAsync(owner, item.Context.CorrelationId));
                Assert.Null(await durableEvents.GetAsync(Guid.NewGuid(), item.Id));
            }
            Assert.Empty(await durableEvents.ListForUserAsync(Guid.NewGuid()));
            Assert.Equal(2, (await new EfPaperStrategyDecisions(restartedDb).ListForUserAsync(owner)).Count);
            Assert.Equal(2, (await new EfPaperTradeIntents(restartedDb).ListForUserAsync(owner)).Count);
            Assert.Equal(2, (await new EfPaperRiskEvaluations(restartedDb).ListForUserAsync(owner)).Count);
            Assert.Equal(2, (await new EfPaperExecutionCommands(restartedDb).ListForUserAsync(owner)).Count);
            Assert.Equal(2, (await new EfPaperPortfolioUpdates(restartedDb).ListForUserAsync(owner)).Count);
        }
    }

    [Fact]
    public async Task PaperStageRepositoryRejectsLiveAndIncorrectStage()
    {
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var repository = new EfPaperMarketEvents(db);
        var eventPayload = new MarketEvent(Guid.NewGuid(), "BTC/USD", CandleInterval.OneMinute,
            new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), 100m, 1m, true);
        var live = new PipelineRecord<MarketEvent>(
            Guid.NewGuid(), new PipelineContext(Guid.NewGuid(), TradingMode.Live, "live"),
            PipelineStage.MarketEvent, eventPayload, eventPayload.EventTimeUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(live));
        var wrongStage = new PipelineRecord<MarketEvent>(Guid.NewGuid(),
            new PipelineContext(Guid.NewGuid(), TradingMode.Paper, "paper"),
            PipelineStage.ExecutionCommand, eventPayload, eventPayload.EventTimeUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(wrongStage));
        Assert.Empty(db.AuditEvents);
    }

    private sealed class UnexpectedConfigurationSource : IExperimentResearchGroupConfigurationSource
    {
        public Task<ExperimentResearchGroupConfiguration?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unprotected worker must not reach strategy configuration.");
    }

    private sealed class MissingPaperPlans : IExperimentPaperPlanEvidenceRepository
    {
        public Task SaveAsync(ExperimentPaperPlanEvidence evidence, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An unprotected worker must not save another paper plan.");

        public Task<IReadOnlyList<ExperimentPaperPlanEvidence>> ListAsync(
            Guid userId, Guid workerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExperimentPaperPlanEvidence>>([]);
    }

    private sealed class RecordingPaperLedger : IPaperTradingLedgerRepository
    {
        public List<PaperTradingLedgerEntry> Entries { get; } = [];
        public Task AddAsync(Guid userId, PaperTradingLedgerEntry entry,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyCollection<PaperTradingLedgerEntry>> ListAsync(
            Guid userId, Guid workerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<PaperTradingLedgerEntry>>(
                Entries.Where(entry => entry.WorkerId == workerId).ToArray());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RisingHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>([
                new TradablePair("COINEUR", "COIN/EUR", "COIN", "EUR", true, .0001m, .0001m, .0001m, 10m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = TimeSpan.FromMinutes((int)interval);
            var close = new DateTimeOffset(
                boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(
                Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                    .Select(index =>
                    {
                        var open = close - TimeSpan.FromTicks(
                            duration.Ticks * (ApprovedConsensusStrategyProfiles.RequiredHistory - index));
                        var price = 100m + index;
                        var low = interval == CandleInterval.FiveMinutes
                            && index == ApprovedConsensusStrategyProfiles.RequiredHistory - 2
                                ? price - 12m : price - 1m;
                        return new Candle(
                            symbol, interval, open, open + duration,
                            price, price + 1m, low, price + .5m,
                            interval == CandleInterval.OneDay ? 20_000m
                                : index == ApprovedConsensusStrategyProfiles.RequiredHistory - 1
                                    ? 200m : 100m,
                            true, false);
                    }).ToArray());
        }
    }

    private sealed class BollingerHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>([
                new TradablePair("COINEUR", "COIN/EUR", "COIN", "EUR", true, .0001m, .0001m, .0001m, 10m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = TimeSpan.FromMinutes((int)interval);
            var aligned = new DateTimeOffset(boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0,
                ApprovedConsensusStrategyProfiles.RequiredHistory).Select(index =>
            {
                var open = aligned - TimeSpan.FromTicks(duration.Ticks
                    * (ApprovedConsensusStrategyProfiles.RequiredHistory - index));
                var excursion = interval == CandleInterval.FifteenMinutes && index == 318;
                var reentry = interval == CandleInterval.FifteenMinutes && index == 319;
                return new Candle(symbol, interval, open, open + duration,
                    reentry ? 92m : 100m,
                    reentry ? 96m : 101m,
                    excursion || reentry ? 92m : 99m,
                    excursion ? 92m : reentry ? 95.5m : 100m,
                    interval == CandleInterval.OneDay ? 20_000m : 100m, true, false);
            }).ToArray());
        }
    }

    private sealed class RsiPullbackHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
    {
        private static decimal SignalClose(int index) =>
            index <= 304 ? 200m : index == 319 ? 196m
                : 201m - (index - 305) / 2 - ((index - 305) % 2 == 1 ? 2m : 0m);

        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>([
                new TradablePair("COINEUR", "COIN/EUR", "COIN", "EUR", true, .0001m, .0001m, .0001m, 10m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = TimeSpan.FromMinutes((int)interval);
            var aligned = new DateTimeOffset(boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0,
                ApprovedConsensusStrategyProfiles.RequiredHistory).Select(index =>
            {
                var openTime = aligned - TimeSpan.FromTicks(duration.Ticks
                    * (ApprovedConsensusStrategyProfiles.RequiredHistory - index));
                var close = interval == CandleInterval.FiveMinutes
                    ? SignalClose(index) : 100m + index * .3m;
                var open = interval == CandleInterval.FiveMinutes
                    ? SignalClose(Math.Max(0, index - 1))
                    : 100m + Math.Max(0, index - 1) * .3m;
                return new Candle(symbol, interval, openTime, openTime + duration,
                    open, Math.Max(open, close) + .5m, Math.Min(open, close) - .5m,
                    close, interval == CandleInterval.OneDay ? 20_000m : 100m, true, false);
            }).ToArray());
        }
    }

    private sealed class MacdCrossHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
    {
        private static decimal SignalClose(int index) =>
            index <= 304 ? 200m : index == 319 ? 200.6m
                : 200m - (index - 304) * .1m;

        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>([
                new TradablePair("COINEUR", "COIN/EUR", "COIN", "EUR", true, .0001m, .0001m, .0001m, 10m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = TimeSpan.FromMinutes((int)interval);
            var aligned = new DateTimeOffset(boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0,
                ApprovedConsensusStrategyProfiles.RequiredHistory).Select(index =>
            {
                var openTime = aligned - TimeSpan.FromTicks(duration.Ticks
                    * (ApprovedConsensusStrategyProfiles.RequiredHistory - index));
                var close = interval == CandleInterval.FifteenMinutes
                    ? SignalClose(index) : 100m + index * .3m;
                var open = interval == CandleInterval.FifteenMinutes
                    ? SignalClose(Math.Max(0, index - 1))
                    : 100m + Math.Max(0, index - 1) * .3m;
                return new Candle(symbol, interval, openTime, openTime + duration,
                    open, Math.Max(open, close) + .5m, Math.Min(open, close) - .5m,
                    close, interval == CandleInterval.OneDay ? 20_000m : 100m, true, false);
            }).ToArray());
        }
    }

    private sealed class CompressionBreakoutHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>([
                new TradablePair("COINEUR", "COIN/EUR", "COIN", "EUR", true, .0001m, .0001m, .0001m, 10m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = TimeSpan.FromMinutes((int)interval);
            var aligned = new DateTimeOffset(boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0,
                ApprovedConsensusStrategyProfiles.RequiredHistory).Select(index =>
            {
                var openTime = aligned - TimeSpan.FromTicks(duration.Ticks
                    * (ApprovedConsensusStrategyProfiles.RequiredHistory - index));
                var compressed = interval == CandleInterval.FifteenMinutes && index >= 285 && index < 319;
                var breakout = interval == CandleInterval.FifteenMinutes && index == 319;
                var close = breakout ? 100.3m : compressed ? 100m
                    : interval == CandleInterval.FifteenMinutes ? index % 2 == 0 ? 105m : 95m
                    : 100m + index * .3m;
                var open = breakout || compressed ? 100m
                    : interval == CandleInterval.FifteenMinutes ? index % 2 == 0 ? 95m : 105m
                    : 100m + Math.Max(0, index - 1) * .3m;
                var range = breakout || compressed ? .1m : .5m;
                return new Candle(symbol, interval, openTime, openTime + duration,
                    open, Math.Max(open, close) + range, Math.Min(open, close) - range,
                    close, interval == CandleInterval.OneDay ? 20_000m
                        : breakout ? 200m : 100m, true, false);
            }).ToArray());
        }
    }

    private sealed class RelativeStrengthHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
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
            cancellationToken.ThrowIfCancellationRequested();
            var duration = TimeSpan.FromMinutes((int)interval);
            var close = new DateTimeOffset(
                boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(
                Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                    .Select(index =>
                    {
                        var open = close.AddTicks(duration.Ticks *
                            (index - ApprovedConsensusStrategyProfiles.RequiredHistory));
                        var price = interval switch
                        {
                            CandleInterval.OneDay when symbol == "XBT/EUR" => 100m + index * .04m,
                            CandleInterval.OneDay => 100m + index * .05m
                                + (index >= 315 ? (index - 315) * .25m : 0m),
                            CandleInterval.FourHours when index <= 305 => 100m + index * .3m,
                            CandleInterval.FourHours when index < 319 => 191.5m - (index - 305) * .2m,
                            CandleInterval.FourHours => 189.1m,
                            CandleInterval.OneMinute or CandleInterval.OneHour when index == 319 => 189.4m,
                            CandleInterval.OneHour => 189.1m - (318 - index) * .001m,
                            _ => 189.1m
                        };
                        var range = interval == CandleInterval.OneHour ? .1m : 1m;
                        return new Candle(symbol, interval, open, open.Add(duration),
                            price, price + range, price - range, price, 20_000m, true, false);
                    })
                    .Where(candle => candle.OpenTimeUtc >= sinceUtc)
                    .ToArray());
        }
    }

    private sealed class MomentumHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>(
            [
                new TradablePair("XBTEUR", "XBT/EUR", "XBT", "EUR", true, .0001m, .0001m, .0001m, 10m),
                new TradablePair("ETHEUR", "ETH/EUR", "ETH", "EUR", true, .0001m, .0001m, .0001m, 10m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duration = TimeSpan.FromMinutes((int)interval);
            var close = new DateTimeOffset(
                boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(
                Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                    .Select(index =>
                    {
                        var open = close.AddTicks(duration.Ticks *
                            (index - ApprovedConsensusStrategyProfiles.RequiredHistory));
                        var price = 100m + index * (symbol == "XBT/EUR" ? .3m : .04m);
                        var low = symbol == "XBT/EUR" && interval == CandleInterval.OneDay && index == 316
                            ? price - 4m : price - 1m;
                        return new Candle(symbol, interval, open, open.Add(duration),
                            price, price + 1m, low, price, 20_000m, true, false);
                    })
                    .Where(candle => candle.OpenTimeUtc >= sinceUtc)
                    .ToArray());
        }
    }

    private sealed class ThreeSwingHistory(DateTimeOffset boundary) : IHistoricalCandleSource, ITradablePairSource
    {
        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>(
            [
                new TradablePair("XBTEUR", "XBT/EUR", "XBT", "EUR", true, .0001m, .0001m, .0001m, .1m)
            ]);

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol, CandleInterval interval, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (signal, hourly, fourHourly) = ThreeSwingChannelDivergenceModelTests.BullishEvidence();
            var pattern = interval switch
            {
                CandleInterval.FiveMinutes => signal,
                CandleInterval.OneHour => hourly,
                CandleInterval.FourHours => fourHourly,
                _ => null
            };
            if (pattern is not null)
            {
                var shift = boundary - new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
                return Task.FromResult<IReadOnlyList<Candle>>(pattern.Select(candle =>
                    new Candle(symbol, interval, candle.OpenTimeUtc + shift,
                        candle.CloseTimeUtc + shift, candle.Open, candle.High, candle.Low,
                        candle.Close, candle.Volume, true, false))
                    .Where(candle => candle.OpenTimeUtc >= sinceUtc).ToArray());
            }
            var duration = TimeSpan.FromMinutes((int)interval);
            var close = new DateTimeOffset(
                boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
            return Task.FromResult<IReadOnlyList<Candle>>(
                Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                    .Select(index =>
                    {
                        var open = close.AddTicks(duration.Ticks *
                            (index - ApprovedConsensusStrategyProfiles.RequiredHistory));
                        var price = interval == CandleInterval.OneDay ? 100m + index * .1m : 130m;
                        return new Candle(symbol, interval, open, open.Add(duration),
                            price, price + 1m, price - 1m, price,
                            interval == CandleInterval.OneDay ? 20_000m : 100m, true, false);
                    })
                    .Where(candle => candle.OpenTimeUtc >= sinceUtc)
                    .ToArray());
        }
    }
}
