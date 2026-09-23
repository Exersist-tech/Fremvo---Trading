using System.Security.Cryptography;
using System.Text;
using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Strategies;
using Trading.Strategies.Approvals;

namespace Trading.ArchitectureTests;

public sealed class PaperExperimentWorkerRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstrumentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly string[] ExpectedPhase5BFamilies =
    [
        "platform.ema-trend-continuation",
        "platform.donchian-breakout-ensemble",
        "platform.bollinger-mean-reversion",
        "platform.rsi-pullback",
        "platform.macd-volume",
        "platform.volatility-compression-breakout",
        "platform.rsi-macd-confluence",
        "platform.ema-rsi-trend",
        "platform.bollinger-macd-recovery",
        "platform.donchian-volume-breakout",
        "platform.ema-volume-pullback",
        "platform.cross-sectional-momentum-rotation",
        "platform.relative-strength-pullback-rotation",
        "platform.session-conditioned-breakout",
        "platform.regime-switching-ensemble"
    ];

    private sealed class FixedCandleSource : IExperimentCandleSeriesSource
    {
        private readonly ExperimentCandleSeriesResult _result;

        public FixedCandleSource(ExperimentCandleSeriesResult result) => _result = result;

        public Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
            ExperimentCandleSeriesRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }

    [Fact]
    public async Task EveryPhase5BFamilyResolvesDeterministicallyWithoutChangingWorkerState()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var runner = new PaperExperimentWorkerRunner(new FixedCandleSource(AvailableSeries()), registry);

        Assert.Equal(ExpectedPhase5BFamilies.OrderBy(value => value), registry.Definitions.Select(value => value.FamilyId).OrderBy(value => value));
        foreach (var definition in registry.Definitions)
        {
            var worker = Worker("{}", definition.FamilyId);
            var configuration = Configuration(worker, definition);
            var first = await runner.AnalyzeAsync(worker, configuration, configuration.Assignments.Single(), Now);
            var second = await runner.AnalyzeAsync(worker, configuration, configuration.Assignments.Single(), Now);

            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.Reason, second.Reason);
            Assert.True(first.Outcome is ExperimentAnalysisOutcome.Analyzed or ExperimentAnalysisOutcome.Blocked or ExperimentAnalysisOutcome.NoCondition);
            if (first.Outcome == ExperimentAnalysisOutcome.Analyzed)
                Assert.NotNull(first.Evidence);
            else if (first.Outcome == ExperimentAnalysisOutcome.Blocked)
                Assert.Contains(definition.FamilyId, first.Reason, StringComparison.Ordinal);
            Assert.Equal(0m, worker.PositionQuantity);
            Assert.Empty(worker.Ledger);
        }
    }

    [Fact]
    public async Task UnknownFamilyAndVersionOrParameterMismatchAreExplicitlyBlocked()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.First();
        var unknownWorker = new ExperimentWorker(Guid.NewGuid(), Guid.NewGuid(), "worker", "not-platform-owned", "BTC/USD", 1_000m, Now, 1);
        unknownWorker.UpdateStrategyParameters("""{"fastPeriod":2,"slowPeriod":3}""");
        unknownWorker.Start();
        var unknown = Configuration(unknownWorker, definition, familyId: "not-platform-owned");
        var runner = new PaperExperimentWorkerRunner(new FixedCandleSource(AvailableSeries()), registry);

        var unknownResult = await runner.AnalyzeAsync(unknownWorker, unknown, unknown.Assignments.Single(), Now);
        var worker = Worker("{}", definition.FamilyId);
        var matching = Configuration(worker, definition);
        worker.UpdateStrategyParameters("""{"fastPeriod":3,"slowPeriod":4}""");
        var mismatchResult = await runner.AnalyzeAsync(worker, matching, matching.Assignments.Single(), Now);

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, unknownResult.Outcome);
        Assert.Contains("family/version", unknownResult.Reason, StringComparison.Ordinal);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, mismatchResult.Outcome);
        Assert.Contains("fingerprint", mismatchResult.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedGateAndForeignGroupAreExplicitlyBlocked()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.First();
        var worker = Worker("{}", definition.FamilyId);
        var rejected = Configuration(worker, definition, acceptedGate: false);
        var runner = new PaperExperimentWorkerRunner(new FixedCandleSource(AvailableSeries()), registry);

        var rejectedResult = await runner.AnalyzeAsync(worker, rejected, rejected.Assignments.Single(), Now);
        var otherWorker = Worker("{}", definition.FamilyId);
        var foreignResult = await runner.AnalyzeAsync(otherWorker, rejected, rejected.Assignments.Single(), Now);

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, rejectedResult.Outcome);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, foreignResult.Outcome);
        Assert.Empty(worker.Ledger);
        Assert.Empty(otherWorker.Ledger);
    }

    [Fact]
    public void RegistryExposesNoRuntimeRegistrationOrSuppliedCodeSurface()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();

        Assert.Equal(15, registry.Definitions.Count);
        Assert.Contains(registry.Definitions, definition =>
            definition.FamilyId == "platform.rsi-macd-confluence");
        Assert.DoesNotContain(typeof(ApprovedExperimentStrategyRegistry).GetMethods(), method =>
            method.Name.Contains("Register", StringComparison.OrdinalIgnoreCase)
            || method.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
    }

    [Theory]
    [InlineData("platform.rsi-macd-confluence")]
    [InlineData("platform.ema-rsi-trend")]
    [InlineData("platform.bollinger-macd-recovery")]
    [InlineData("platform.donchian-volume-breakout")]
    [InlineData("platform.ema-volume-pullback")]
    public void HybridStrategiesEvaluateDeterministicallyWithSufficientClosedHistory(string strategyId)
    {
        var candles = Enumerable.Range(0, 40)
            .Select(index =>
            {
                var openTime = Now.AddHours(-40 + index);
                var baseline = 100m + (index * 0.2m);
                var close = baseline + (index % 3 == 0 ? -0.3m : 0.4m);
                return new Candle(
                    "BTC/USD",
                    CandleInterval.OneHour,
                    openTime,
                    openTime.AddHours(1),
                    baseline,
                    Math.Max(baseline, close) + 0.5m,
                    Math.Min(baseline, close) - 0.5m,
                    close,
                    100m + index,
                    true,
                    false);
            })
            .ToArray();
        var series = new ExperimentCandleSeries("BTC/USD", CandleInterval.OneHour, Now, candles);
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();

        var first = registry.EvaluateHistorical(strategyId, series, "{}");
        var second = registry.EvaluateHistorical(strategyId, series, "{}");

        Assert.NotEqual(ExperimentAnalysisOutcome.Blocked, first.Outcome);
        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public async Task AttestedObservationsMapDeterministicallyAndRequireFavorableMarkForAdds()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.First();
        var worker = Worker("{}", definition.FamilyId);
        var configuration = Configuration(worker, definition);
        var runner = new PaperExperimentWorkerRunner(new FixedCandleSource(AvailableSeries()), registry);
        var analysis = await runner.AnalyzeAsync(worker, configuration, configuration.Assignments.Single(), Now);
        var series = AvailableSeries().Series!;
        var candle = series.Candles[^1];
        var identity = new ExperimentClosedCandleIdentity(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc, Now);
        var ledger = new InMemoryExperimentDecisionLedger();
        var policy = new ExperimentDecisionPolicy(ledger, new FakeTimeProvider(Now));
        var snapshot = new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, 0m, Now);

        var first = await policy.DecideAsync(worker, configuration, configuration.Assignments.Single(), analysis, snapshot, identity);
        var repeated = await policy.DecideAsync(worker, configuration, configuration.Assignments.Single(), analysis, snapshot, identity);
        var noMark = await new ExperimentDecisionPolicy(new InMemoryExperimentDecisionLedger(), new FakeTimeProvider(Now)).DecideAsync(
            worker, configuration, configuration.Assignments.Single(), analysis, snapshot with { PositionQuantity = 1m }, identity);
        var neutralLedgerPolicy = new ExperimentDecisionPolicy(new InMemoryExperimentDecisionLedger(), new FakeTimeProvider(Now));
        var preservedNeutral = await neutralLedgerPolicy.DecideAsync(
            worker, configuration, configuration.Assignments.Single(), analysis, snapshot with { PositionQuantity = 1m }, identity);
        worker.ApplyPaperTrade(1m, 100m, 0m, "buy", Now);
        var postFillSameCandle = await policy.DecideAsync(
            worker, configuration, configuration.Assignments.Single(), analysis, snapshot with { PositionQuantity = 1m }, identity);
        worker.RecordFavorablePaperMark(110m);
        var favorableAdd = await new ExperimentDecisionPolicy(new InMemoryExperimentDecisionLedger(), new FakeTimeProvider(Now)).DecideAsync(
            worker, configuration, configuration.Assignments.Single(), analysis, snapshot with { PositionQuantity = 1m }, identity);
        var conflictingLaterAdd = await neutralLedgerPolicy.DecideAsync(
            worker, configuration, configuration.Assignments.Single(), analysis, snapshot with { PositionQuantity = 1m }, identity);
        var changedCandles = series.Candles
            .Select((item, index) => index == series.Candles.Count - 1
                ? new Candle(
                    item.Symbol,
                    item.Interval,
                    item.OpenTimeUtc,
                    item.CloseTimeUtc,
                    item.Open,
                    item.High,
                    Math.Min(item.Low, item.Open - 2m),
                    item.Open - 2m,
                    item.Volume,
                    item.IsClosed,
                    item.IsDerived)
                : item)
            .ToArray();
        var changedRunner = new PaperExperimentWorkerRunner(
            new FixedCandleSource(ExperimentCandleSeriesResult.Available(
                new ExperimentCandleSeries(series.Symbol, series.Interval, series.AsOfUtc, changedCandles))),
            registry);
        var changedEvidence = await changedRunner.AnalyzeAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            Now);

        Assert.Equal(ExperimentProposalAction.Open, first.Proposal.Action);
        Assert.Equal(first, repeated);
        Assert.Equal(first, postFillSameCandle);
        Assert.Equal(ExperimentProposalAction.Neutral, noMark.Proposal.Action);
        Assert.Equal(ExperimentProposalAction.Neutral, preservedNeutral.Proposal.Action);
        Assert.Equal(preservedNeutral, conflictingLaterAdd);
        Assert.Equal(ExperimentProposalAction.Add, favorableAdd.Proposal.Action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.DecideAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            changedEvidence,
            snapshot with { PositionQuantity = 1m },
            identity));
        Assert.Single(worker.Ledger);
    }

    [Fact]
    public async Task NoConditionIsNeutralAndUnattestedOrMismatchedEvidenceIsRejected()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.First();
        var worker = Worker("{}", definition.FamilyId);
        var configuration = Configuration(worker, definition);
        var now = Now;
        var flat = new Candle("BTC/USD", CandleInterval.OneHour, now.AddHours(-1), now, 100m, 100m, 100m, 100m, 1m, true, false);
        var runner = new PaperExperimentWorkerRunner(new FixedCandleSource(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneHour, now, new[] { flat }))), registry);
        var result = await runner.AnalyzeAsync(worker, configuration, configuration.Assignments.Single(), now);
        var policy = new ExperimentDecisionPolicy(new InMemoryExperimentDecisionLedger(), new FakeTimeProvider(now));
        var snapshot = new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, 0m, now);
        var identity = new ExperimentClosedCandleIdentity(flat.Symbol, flat.Interval, flat.OpenTimeUtc, flat.CloseTimeUtc, now);

        var neutral = await policy.DecideAsync(worker, configuration, configuration.Assignments.Single(), result, snapshot, identity);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.Equal(ExperimentProposalAction.Neutral, neutral.Proposal.Action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.DecideAsync(worker, configuration, configuration.Assignments.Single(),
            ExperimentAnalysisResult.Analyzed("forged", 1m), snapshot, identity));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.DecideAsync(worker, configuration, configuration.Assignments.Single(),
            result, snapshot with { WorkerId = Guid.NewGuid() }, identity));
    }

    [Fact]
    public async Task PaperAnalysisRequiresPrimarySignalAndAnotherTimeframeConfirmation()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.Single(candidate =>
            candidate.FamilyId == "platform.ema-trend-continuation");
        var worker = Worker("{}", definition.FamilyId);
        var configuration = Configuration(
            worker,
            definition,
            signalInterval: CandleInterval.FifteenMinutes,
            allowedIntervals: PaperTrainingAutoSelectionService.ApprovedIntervals);
        var source = new MultiIntervalCandleSource();
        var runner = new PaperExperimentWorkerRunner(source, registry);

        var result = await runner.AnalyzeAcrossPaperTimeframesAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            Now);

        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, result.Outcome);
        Assert.Equal(CandleInterval.FifteenMinutes, result.Evidence!.Interval);
        Assert.Equal(64, result.Evidence.ContextFingerprint.Length);
        Assert.Equal(
            PaperTrainingAutoSelectionService.ApprovedIntervals.Order(),
            source.RequestedIntervals.Order());
        Assert.Contains("Multi-timeframe confirmation passed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaperAnalysisFailsClosedWhenARequiredTimeframeIsUnavailable()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.Single(candidate =>
            candidate.FamilyId == "platform.ema-trend-continuation");
        var worker = Worker("{}", definition.FamilyId);
        var configuration = Configuration(
            worker,
            definition,
            signalInterval: CandleInterval.FifteenMinutes,
            allowedIntervals: PaperTrainingAutoSelectionService.ApprovedIntervals);
        var source = new MultiIntervalCandleSource(CandleInterval.ThirtyMinutes);
        var runner = new PaperExperimentWorkerRunner(source, registry);

        var result = await runner.AnalyzeAcrossPaperTimeframesAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            Now);

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.NotNull(result.Evidence);
        Assert.Contains("30m confirmation evidence is unavailable", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportingTimeframesAreCutOffAtThePrimaryCandleClose()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.Single(candidate =>
            candidate.FamilyId == "platform.ema-trend-continuation");
        var worker = Worker("{}", definition.FamilyId);
        var configuration = Configuration(
            worker,
            definition,
            signalInterval: CandleInterval.FifteenMinutes,
            allowedIntervals: PaperTrainingAutoSelectionService.ApprovedIntervals);
        var primaryClose = Now.AddMinutes(-5);
        var source = new MultiIntervalCandleSource(primaryCloseUtc: primaryClose);
        var runner = new PaperExperimentWorkerRunner(source, registry);

        var result = await runner.AnalyzeAcrossPaperTimeframesAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            Now);

        Assert.NotEqual(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.Equal(Now, source.Requests[0].AsOfUtc);
        Assert.All(source.Requests.Skip(1), request => Assert.Equal(primaryClose, request.AsOfUtc));
        Assert.Equal(primaryClose, result.Evidence!.AsOfUtc);
    }

    [Fact]
    public async Task SupplementalProviderEvaluatesEachFixedFamilyAndBlocksAnIncompleteUniverse()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var series = FixedUniverseSeries();
        var source = new FixedUniverseCandleSource(series);
        var provider = new PlatformSupplementalExperimentEvidenceProvider(source);
        var fixedFamilySeries = series["BTC/USD"].Series!;
        var inexactFixedFamilySeries = new ExperimentCandleSeries(
            fixedFamilySeries.Symbol,
            fixedFamilySeries.Interval,
            fixedFamilySeries.AsOfUtc.AddSeconds(1),
            fixedFamilySeries.Candles);
        Assert.Null(await provider.EvaluateAsync(
            "platform.rsi-pullback",
            inexactFixedFamilySeries,
            Configuration(
                Worker("{}", "platform.rsi-pullback"),
                registry.Definitions.Single(candidate => candidate.FamilyId == "platform.rsi-pullback"))
                .Assignments.Single().Provenance));

        foreach (var family in new[]
                 {
                     "platform.cross-sectional-momentum-rotation",
                     "platform.relative-strength-pullback-rotation",
                     "platform.session-conditioned-breakout",
                     "platform.regime-switching-ensemble"
                 })
        {
            var definition = registry.Definitions.Single(candidate => candidate.FamilyId == family);
            var worker = Worker("{}", family);
            var configuration = Configuration(worker, definition);
            var result = await provider.EvaluateAsync(
                family, series["BTC/USD"].Series!, configuration.Assignments.Single().Provenance);

            Assert.NotNull(result);
            Assert.True(result!.Outcome is ExperimentAnalysisOutcome.Analyzed
                or ExperimentAnalysisOutcome.NoCondition or ExperimentAnalysisOutcome.Blocked);
        }

        var missing = new PlatformSupplementalExperimentEvidenceProvider(
            new FixedUniverseCandleSource(new Dictionary<string, ExperimentCandleSeriesResult>
            {
                ["BTC/USD"] = series["BTC/USD"],
                ["ETH/USD"] = series["ETH/USD"]
            }));
        var crossDefinition = registry.Definitions.Single(candidate => candidate.FamilyId == "platform.cross-sectional-momentum-rotation");
        var crossWorker = Worker("{}", crossDefinition.FamilyId);
        var crossConfiguration = Configuration(crossWorker, crossDefinition);
        var runner = new PaperExperimentWorkerRunner(source, registry, provider);
        var analyzed = await runner.AnalyzeAsync(crossWorker, crossConfiguration,
            crossConfiguration.Assignments.Single(), Now);
        Assert.NotEqual(ExperimentAnalysisOutcome.Blocked, analyzed.Outcome);

        var blocked = await missing.EvaluateAsync(crossDefinition.FamilyId, series["BTC/USD"].Series!,
            crossConfiguration.Assignments.Single().Provenance);

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, blocked!.Outcome);
        Assert.Contains("SOL/USD", blocked.Reason, StringComparison.Ordinal);

        var stale = new ExperimentCandleSeries("BTC/USD", CandleInterval.OneHour, Now,
            series["BTC/USD"].Series!.Candles.Take(90).ToArray());
        foreach (var family in new[]
                 {
                     "platform.session-conditioned-breakout",
                     "platform.regime-switching-ensemble"
                 })
        {
            var definition = registry.Definitions.Single(candidate => candidate.FamilyId == family);
            var worker = Worker("{}", family);
            var configuration = Configuration(worker, definition);
            var staleResult = await provider.EvaluateAsync(family, stale, configuration.Assignments.Single().Provenance);
            Assert.Equal(ExperimentAnalysisOutcome.Blocked, staleResult!.Outcome);
            Assert.Contains("exactly", staleResult.Reason, StringComparison.Ordinal);
        }
    }

    private sealed class FixedUniverseCandleSource : IExperimentCandleSeriesSource
    {
        private readonly IReadOnlyDictionary<string, ExperimentCandleSeriesResult> _series;
        public FixedUniverseCandleSource(IReadOnlyDictionary<string, ExperimentCandleSeriesResult> series) => _series = series;
        public Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
            ExperimentCandleSeriesRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_series.TryGetValue(request.Symbol, out var result)
                ? result
                : ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.NoData));
    }

    private static Dictionary<string, ExperimentCandleSeriesResult> FixedUniverseSeries() =>
        new[]
            {
                ("BTC/USD", 100m), ("ETH/USD", 90m), ("SOL/USD", 80m),
                ("XRP/EUR", 70m), ("TRX/EUR", 60m), ("DOGE/EUR", 50m), ("ADA/EUR", 40m)
            }
            .ToDictionary(pair => pair.Item1, pair =>
            {
                var candles = Enumerable.Range(0, 91).Select(index =>
                {
                    var open = Now.AddHours(-91 + index);
                    var close = pair.Item2 + index;
                    return new Candle(pair.Item1, CandleInterval.OneHour, open, open.AddHours(1),
                        close - 1m, close + 1m, close - 2m, close, 1m, true, false);
                }).ToArray();
                return ExperimentCandleSeriesResult.Available(
                    new ExperimentCandleSeries(pair.Item1, CandleInterval.OneHour, Now, candles));
            });

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class MultiIntervalCandleSource(
        CandleInterval? unavailable = null,
        DateTimeOffset? primaryCloseUtc = null)
        : IExperimentCandleSeriesSource
    {
        private readonly List<ExperimentCandleSeriesRequest> _requests = [];

        public IReadOnlyList<CandleInterval> RequestedIntervals => _requests.Select(request => request.Interval).ToArray();
        public List<ExperimentCandleSeriesRequest> Requests => _requests;

        public Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
            ExperimentCandleSeriesRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Add(request);
            if (request.Interval == unavailable)
                return Task.FromResult(ExperimentCandleSeriesResult.Blocked(
                    ExperimentCandleSeriesBlockReason.NoData));

            var duration = TimeSpan.FromMinutes((int)request.Interval);
            var closeUtc = request.Interval == CandleInterval.FifteenMinutes && primaryCloseUtc.HasValue
                ? primaryCloseUtc.Value
                : request.AsOfUtc;
            var candles = Enumerable.Range(0, 3)
                .Select(index =>
                {
                    var openTime = closeUtc.AddTicks(duration.Ticks * (index - 3));
                    return new Candle(
                        "BTC/USD",
                        request.Interval,
                        openTime,
                        openTime.Add(duration),
                        100m + index,
                        102m + index,
                        99m + index,
                        101m + index,
                        1m,
                        true,
                        false);
                })
                .ToArray();
            return Task.FromResult(ExperimentCandleSeriesResult.Available(
                new ExperimentCandleSeries("BTC/USD", request.Interval, closeUtc, candles)));
        }
    }

    private static ExperimentWorker Worker(string parameters, string strategyId = "platform.ema-trend-continuation")
    {
        var worker = new ExperimentWorker(Guid.NewGuid(), Guid.NewGuid(), "worker", strategyId, "BTC/USD", 1_000m, Now, 1);
        worker.UpdateStrategyParameters(parameters);
        worker.Start();
        return worker;
    }

    private static ExperimentResearchGroupConfiguration Configuration(
        ExperimentWorker worker,
        ApprovedExperimentStrategyDefinition definition,
        string? familyId = null,
        bool acceptedGate = true,
        CandleInterval signalInterval = CandleInterval.OneHour,
        IReadOnlyList<CandleInterval>? allowedIntervals = null)
    {
        var approval = StrategyApproval.CreateDraft(
            Guid.NewGuid(),
            new StrategyVersion(
                new StrategyTemplateVersionIdentity(familyId ?? definition.FamilyId, definition.Version),
                new StrategyParameterSchemaReference(
                    definition.ParameterSchemaId,
                    definition.ParameterSchemaVersion,
                    definition.ParameterSchemaFingerprint),
                definition.ContentFingerprint,
                Now),
            StrategyApprovalActor.Human(Guid.NewGuid()),
            Now,
            new StrategyApprovalRequirements(
                new[] { new ApprovedInstrumentScope(AssetClass.Cryptocurrency, InstrumentId) },
                3, 1m, 1m, 1m, TimeSpan.FromHours(1),
                allowedIntervals ?? new[] { CandleInterval.OneHour },
                new[] { TradingProductType.Spot },
                new[] { StrategyApprovalMode.Paper },
                new StrategyTimeframeConfiguration(
                    CandleInterval.OneHour,
                    signalInterval,
                    allowedIntervals is null ? CandleInterval.OneHour : CandleInterval.FiveMinutes)));
        var actor = approval.CreatedBy;
        approval = approval.TransitionTo(StrategyApprovalState.UnderReview, actor, Now)
            .TransitionTo(StrategyApprovalState.Approved, actor, Now, actor);
        var fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var dataset = new HistoricalDataset("dataset", "research", worker.MarketSymbol, "1H", Now.AddDays(-1), Now.AddHours(-1), 3, fingerprint, "v1", Now);
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("research", fingerprint, Now),
            new StrategyApprovalEvidence(InstrumentId, AssetClass.Cryptocurrency, 3, 10m, 0.1m, 0.01m, Now),
            acceptedGate, 1m, 1m, true, 1m, 1m, 1m, fingerprint);
        var gates = StrategyRejectionGateEngine.CreatePlatformDefault().Evaluate(
            new StrategyRejectionGateEvaluationInput(approval, approval.Requirements?.TimeframeConfiguration, TradingProductType.Spot, StrategyApprovalMode.Paper, evidence, Now));
        var provenance = new ExperimentResearchProvenance(
            approval,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(worker.StrategyParameters.Trim()))),
            dataset,
            new ExperimentClassifierReference("platform-regime", 1, fingerprint),
            evidence.Provenance,
            gates);
        return ExperimentResearchGroupConfiguration.Create(worker.UserId, 1, new[] { worker }, provenance);
    }

    private static ExperimentCandleSeriesResult AvailableSeries()
    {
        var candles = Enumerable.Range(0, 3).Select(index =>
        {
            var open = Now.AddHours(-3 + index);
            return new Candle("BTC/USD", CandleInterval.OneHour, open, open.AddHours(1), 100m + index, 101m + index, 99m + index, 101m + index, 1m, true, false);
        }).ToArray();
        return ExperimentCandleSeriesResult.Available(new ExperimentCandleSeries("BTC/USD", CandleInterval.OneHour, Now, candles));
    }
}
