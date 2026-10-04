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
    private static readonly string[] ExpectedConsensusFamilies =
    [
        "platform.ema-trend-continuation",
        "platform.donchian-breakout-ensemble",
        "platform.bollinger-mean-reversion",
        "platform.rsi-pullback",
        "platform.macd-volume",
        "platform.volatility-compression-breakout",
        "platform.cross-sectional-momentum-rotation",
        "platform.relative-strength-pullback-rotation",
        "platform.session-conditioned-breakout",
        "platform.regime-switching-ensemble",
        "platform.three-swing-channel-divergence"
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

        Assert.Equal(ExpectedConsensusFamilies.OrderBy(value => value), registry.Definitions.Select(value => value.FamilyId).OrderBy(value => value));
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
    public async Task SavedInvalidWorkerSettingsBlockAnalysisWithoutThrowingOrChangingParameters()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.Single(candidate =>
            candidate.FamilyId == "platform.ema-trend-continuation");
        const string settings = """{"minimumAgreement":3}""";
        var worker = Worker(settings);
        var configuration = Configuration(worker, definition);
        var result = await new PaperExperimentWorkerRunner(
            new FixedCandleSource(AvailableSeries()), registry)
            .AnalyzeAsync(worker, configuration, configuration.Assignments.Single(), Now);

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.Contains("require review", result.Reason, StringComparison.Ordinal);
        Assert.Equal(settings, worker.StrategyParameters);
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

        Assert.Equal(11, registry.Definitions.Count);
        Assert.DoesNotContain(registry.Definitions, definition =>
            definition.FamilyId == "platform.rsi-macd-confluence");
        Assert.DoesNotContain(typeof(ApprovedExperimentStrategyRegistry).GetMethods(), method =>
            method.Name.Contains("Register", StringComparison.OrdinalIgnoreCase)
            || method.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
    }

    [Theory]
    [InlineData("platform.ema-trend-continuation", 4)]
    [InlineData("platform.donchian-breakout-ensemble", 4)]
    [InlineData("platform.bollinger-mean-reversion", 5)]
    [InlineData("platform.rsi-pullback", 4)]
    [InlineData("platform.macd-volume", 4)]
    [InlineData("platform.volatility-compression-breakout", 4)]
    public void CandleStrategiesEmitExactlyFiveDeterministicChecks(string strategyId, int requiredAgreement)
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

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Value, second.Value);
        Assert.NotNull(first.Consensus);
        Assert.Equal(5, first.Consensus.Checks.Count);
        if (first.Consensus.MandatoryVeto)
            Assert.Equal(ExperimentAnalysisOutcome.Blocked, first.Outcome);
        else
            Assert.Equal(requiredAgreement, first.Consensus.RequiredAgreement);
    }

    [Fact]
    public async Task AttestedObservationsMapDeterministicallyAndRequireFavorableMarkForAdds()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.Definitions.First();
        var worker = Worker("{}", definition.FamilyId);
        var configuration = Configuration(worker, definition);
        var runner = new PaperExperimentWorkerRunner(new MultiIntervalCandleSource(), registry);
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
        var changedRunner = new PaperExperimentWorkerRunner(
            new MultiIntervalCandleSource(changeLatest: true),
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
        Assert.Equal(5, result.Consensus!.Checks.Count);
        Assert.Equal(CandleInterval.FifteenMinutes, result.Evidence!.Interval);
        Assert.Equal(64, result.Evidence.ContextFingerprint.Length);
        Assert.Equal(
            new[] { CandleInterval.FiveMinutes, CandleInterval.FifteenMinutes, CandleInterval.FourHours }.Order(),
            source.RequestedIntervals.Order());
        Assert.Contains("bullish consensus", result.Reason, StringComparison.Ordinal);
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
        var source = new MultiIntervalCandleSource(CandleInterval.FourHours);
        var runner = new PaperExperimentWorkerRunner(source, registry);

        var result = await runner.AnalyzeAcrossPaperTimeframesAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            Now);

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.Null(result.Evidence);
        Assert.Contains("regime timeframe", result.Reason, StringComparison.Ordinal);
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
        var primaryClose = Now;
        var source = new MultiIntervalCandleSource(primaryCloseUtc: primaryClose);
        var runner = new PaperExperimentWorkerRunner(source, registry);

        var result = await runner.AnalyzeAcrossPaperTimeframesAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            Now.AddMinutes(5));
        var repeated = await runner.AnalyzeAcrossPaperTimeframesAsync(
            worker,
            configuration,
            configuration.Assignments.Single(),
            Now.AddMinutes(10));

        Assert.NotEqual(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.All(source.Requests.Where(request => request.Interval != CandleInterval.FifteenMinutes),
            request => Assert.True(request.AsOfUtc <= primaryClose));
        Assert.Equal(primaryClose, result.Evidence!.AsOfUtc);
        Assert.Equal(result.Evidence.ContextFingerprint, repeated.Evidence!.ContextFingerprint);
        var policy = new ExperimentDecisionPolicy(new InMemoryExperimentDecisionLedger(),
            new FakeTimeProvider(Now.AddMinutes(10)));
        var identity = new ExperimentClosedCandleIdentity(
            result.Evidence.Symbol, result.Evidence.Interval,
            result.Evidence.OpenTimeUtc, result.Evidence.CloseTimeUtc, primaryClose);
        var firstDecision = await policy.DecideAsync(worker, configuration, configuration.Assignments.Single(),
            result, new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, 0m, primaryClose), identity);
        Assert.Equal(ExperimentProposalAction.Open, firstDecision.Proposal.Action);
        worker.ApplyPaperTrade(1m, 100m, 0m, "buy", Now.AddMinutes(1));
        var repeatedDecision = await policy.DecideAsync(worker, configuration, configuration.Assignments.Single(),
            repeated, new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, 1m, primaryClose), identity);
        Assert.Equal(firstDecision, repeatedDecision);
    }

    [Fact]
    public async Task SupplementalProviderEvaluatesCrossSectionalFamiliesAndBlocksAnIncompleteUniverse()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var series = FixedUniverseSeries();
        var source = new FixedUniverseCandleSource(series);
        var provider = new PlatformSupplementalExperimentEvidenceProvider(source);
        var fixedFamilySeries = series["XBT/EUR"].Series!;
        var inexactFixedFamilySeries = new ExperimentCandleSeries(
            fixedFamilySeries.Symbol,
            fixedFamilySeries.Interval,
            fixedFamilySeries.AsOfUtc.AddSeconds(1),
            fixedFamilySeries.Candles);
        Assert.Null(await provider.EvaluateAsync(
            Guid.Empty,
            "platform.rsi-pullback",
            inexactFixedFamilySeries,
            Configuration(
                Worker("{}", "platform.rsi-pullback"),
                registry.Definitions.Single(candidate => candidate.FamilyId == "platform.rsi-pullback"))
                .Assignments.Single().Provenance));

        foreach (var family in new[]
                 {
                     "platform.cross-sectional-momentum-rotation",
                     "platform.relative-strength-pullback-rotation"
                 })
        {
            var definition = registry.Definitions.Single(candidate => candidate.FamilyId == family);
            var worker = Worker("{}", family);
            var configuration = Configuration(worker, definition);
            var result = await provider.EvaluateAsync(
                Guid.Empty,
                family, series["XBT/EUR"].Series!, configuration.Assignments.Single().Provenance);

            Assert.NotNull(result);
            Assert.Equal(ExperimentAnalysisOutcome.Blocked, result!.Outcome);
            Assert.Contains("pinned eligible-universe", result.Reason, StringComparison.Ordinal);
        }

        var missing = new PlatformSupplementalExperimentEvidenceProvider(
            new FixedUniverseCandleSource(new Dictionary<string, ExperimentCandleSeriesResult>
            {
                ["XBT/EUR"] = series["XBT/EUR"],
                ["ETH/EUR"] = series["ETH/EUR"]
            }));
        var crossDefinition = registry.ResolveDefinition("platform.cross-sectional-momentum-rotation", 3);
        var crossWorker = Worker("{}", crossDefinition.FamilyId);
        var crossConfiguration = Configuration(crossWorker, crossDefinition);
        var runner = new PaperExperimentWorkerRunner(source, registry, provider);
        crossWorker = new ExperimentWorker(crossWorker.Id, crossWorker.UserId, crossWorker.Name,
            crossWorker.StrategyId, "XBT/EUR", crossWorker.StartingCash, crossWorker.CreatedAtUtc, crossWorker.RandomSeed);
        crossWorker.Start();
        crossConfiguration = Configuration(crossWorker, crossDefinition);
        var analyzed = await runner.AnalyzeAsync(crossWorker, crossConfiguration,
            crossConfiguration.Assignments.Single(), Now);
        Assert.NotEqual(ExperimentAnalysisOutcome.Blocked, analyzed.Outcome);

        var blocked = await missing.EvaluateAsync(Guid.Empty, crossDefinition.FamilyId, series["XBT/EUR"].Series!,
            crossConfiguration.Assignments.Single().Provenance);

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, blocked!.Outcome);
        Assert.Contains("SOL/EUR", blocked.Reason, StringComparison.Ordinal);

        foreach (var family in new[]
                 {
                     "platform.session-conditioned-breakout",
                     "platform.regime-switching-ensemble"
                 })
        {
            var definition = registry.Definitions.Single(candidate => candidate.FamilyId == family);
            var worker = Worker("{}", family);
            var configuration = Configuration(worker, definition);
            var result = await provider.EvaluateAsync(
                Guid.Empty,
                family,
                series["XBT/EUR"].Series!,
                configuration.Assignments.Single().Provenance);
            if (family == "platform.regime-switching-ensemble")
            {
                Assert.Equal(ExperimentAnalysisOutcome.Blocked, result!.Outcome);
                Assert.Contains("Pinned component", result.Reason, StringComparison.Ordinal);
            }
            else
                Assert.Null(result);
        }
    }

    [Fact]
    public async Task PinnedRegimeComponentReplaysFromTheSameClosedUniverseAndRejectsChangedEvidence()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        const int history = ApprovedConsensusStrategyProfiles.RequiredHistory;
        var dailyAsOf = new DateTimeOffset(Now.Year, Now.Month, Now.Day, 0, 0, 0, TimeSpan.Zero);
        ExperimentCandleSeries Series(string symbol, CandleInterval interval, DateTimeOffset asOf, decimal slope,
            bool pullback = false)
        {
            var duration = TimeSpan.FromMinutes((int)interval);
            var candles = Enumerable.Range(0, history).Select(index =>
            {
                var openTime = asOf.AddTicks(duration.Ticks * (index - history));
                var price = 100m + index * slope + (interval == CandleInterval.OneDay && index >= history - 5
                    ? (index - history + 5) * .25m : 0m);
                if (pullback && index == history - 2)
                    price -= .4m;
                return new Candle(symbol, interval, openTime, openTime.Add(duration),
                    price, price + 1m, price - 1m, price, 20_000m, true, false);
            }).ToArray();
            return new ExperimentCandleSeries(symbol, interval, asOf, candles);
        }
        var regime = Series("ETH/EUR", CandleInterval.OneDay, dailyAsOf, .05m);
        var benchmark = Series("XBT/EUR", CandleInterval.OneDay, dailyAsOf, .04m);
        var signal = Series("ETH/EUR", CandleInterval.FourHours, Now, .05m, pullback: true);
        var execution = Series("ETH/EUR", CandleInterval.OneHour, Now, .05m);
        var component = registry.EvaluateProfile(
            "platform.ema-trend-continuation", regime, signal, execution, "{}");
        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, component.Outcome);
        var evidence = component.Consensus!;
        var canonical = string.Join("|", evidence.Checks.Select(check =>
            $"{check.Id}:{(int)check.Direction}:{check.Rationale}"))
            + $"|{evidence.RequiredAgreement}|{string.Join(",", evidence.RequiredEntryChecks)}";
        var selection = new PaperRegimeComponentSelection(
            "platform.ema-trend-continuation", 3, "{}",
            ["ETH/EUR", "XBT/EUR"], dailyAsOf, Now, CandleInterval.FourHours,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        var definition = registry.Definitions.Single(candidate =>
            candidate.FamilyId == "platform.regime-switching-ensemble");
        var worker = new ExperimentWorker(Guid.NewGuid(), Guid.NewGuid(), "pinned-regime", definition.FamilyId,
            "ETH/EUR", 1_000m, Now, 1);
        worker.Start();
        var original = Configuration(worker, definition, signalInterval: CandleInterval.FourHours)
            .Assignments.Single().Provenance;
        ExperimentResearchProvenance Pinned(PaperRegimeComponentSelection selectionValue) => new(
            original.Approval, original.ParametersFingerprint, original.Dataset, original.Classifier,
            original.EvidenceProvenance, original.GateEvaluation, selectionValue);
        var candles = new Dictionary<(string, CandleInterval), ExperimentCandleSeries>
        {
            [("ETH/EUR", CandleInterval.OneDay)] = regime,
            [("XBT/EUR", CandleInterval.OneDay)] = benchmark,
            [("ETH/EUR", CandleInterval.FourHours)] = signal,
            [("ETH/EUR", CandleInterval.OneHour)] = execution
        };
        var provider = new PlatformSupplementalExperimentEvidenceProvider(
            new PinnedRegimeCandleSource(candles));

        var accepted = await provider.EvaluateAsync(worker.UserId, definition.FamilyId, signal,
            Pinned(selection), "{}", regime);
        var changed = await provider.EvaluateAsync(worker.UserId, definition.FamilyId, signal,
            Pinned(selection with { ComponentDecisionFingerprint = new string('0', 64) }), "{}", regime);
        var stale = await provider.EvaluateAsync(worker.UserId, definition.FamilyId, signal,
            Pinned(selection with { SignalAsOfUtc = Now.AddHours(-4) }), "{}", regime);

        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, accepted!.Outcome);
        Assert.Equal(selection, accepted.SelectedComponent);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, changed!.Outcome);
        Assert.Contains("no longer reproduces", changed.Reason, StringComparison.Ordinal);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, stale!.Outcome);
    }

    [Fact]
    public async Task RevisedRelativeStrengthAttestsTheLaterHourNotTheFourHourSetup()
    {
        const string family = "platform.relative-strength-pullback-rotation";
        const int history = ApprovedConsensusStrategyProfiles.RequiredHistory;
        var confirmationClose = Now.AddHours(1);
        var dailyClose = new DateTimeOffset(Now.Year, Now.Month, Now.Day, 0, 0, 0, TimeSpan.Zero);
        ExperimentCandleSeries Series(string symbol, CandleInterval interval, DateTimeOffset close)
        {
            var duration = TimeSpan.FromMinutes((int)interval);
            var candles = Enumerable.Range(0, history).Select(index =>
            {
                var open = close.AddTicks(duration.Ticks * (index - history));
                var price = interval switch
                {
                    CandleInterval.OneDay when symbol == "XBT/EUR" => 100m + index * .04m,
                    CandleInterval.OneDay => 100m + index * .05m
                        + (index >= history - 5 ? (index - history + 5) * .25m : 0m),
                    CandleInterval.FourHours when index <= 305 => 100m + index * .3m,
                    CandleInterval.FourHours when index < 319 => 191.5m - (index - 305) * .2m,
                    CandleInterval.FourHours => 189.1m,
                    CandleInterval.OneHour when index == 319 => 189.4m,
                    CandleInterval.OneHour => 189.1m - (318 - index) * .001m,
                    _ => throw new InvalidOperationException("Unexpected relative-strength interval.")
                };
                var range = interval == CandleInterval.OneHour ? .1m : 1m;
                return new Candle(symbol, interval, open, open.Add(duration),
                    price, price + range, price - range, price, 20_000m, true, false);
            }).ToArray();
            return new ExperimentCandleSeries(symbol, interval, close, candles);
        }

        var setup = Series("ETH/EUR", CandleInterval.FourHours, Now);
        var confirmed = Series("ETH/EUR", CandleInterval.OneHour, confirmationClose);
        var daily = Series("ETH/EUR", CandleInterval.OneDay, dailyClose);
        var benchmark = Series("XBT/EUR", CandleInterval.OneDay, dailyClose);
        var source = new PinnedRegimeCandleSource(
            new Dictionary<(string, CandleInterval), ExperimentCandleSeries>
            {
                [("ETH/EUR", CandleInterval.OneDay)] = daily,
                [("XBT/EUR", CandleInterval.OneDay)] = benchmark,
                [("ETH/EUR", CandleInterval.FourHours)] = setup,
                [("ETH/EUR", CandleInterval.OneHour)] = confirmed
            });
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var definition = registry.ResolveDefinition(family, 5);
        var worker = new ExperimentWorker(Guid.NewGuid(), Guid.NewGuid(),
            "relative worker", family, "ETH/EUR", 1_000m, Now, 1);
        worker.Start();
        var original = Configuration(worker, definition, signalInterval: CandleInterval.FourHours)
            .Assignments.Single().Provenance;
        ExperimentResearchProvenance Pin(DateTimeOffset admissionClose) => new(original.Approval,
            original.ParametersFingerprint, original.Dataset, original.Classifier,
            original.EvidenceProvenance, original.GateEvaluation,
            rankingUniverseSymbols: ["ETH/EUR", "XBT/EUR"],
            admissionCloseUtc: admissionClose);
        var pinned = Pin(confirmationClose);
        var group = ExperimentResearchGroupConfiguration.Create(worker.UserId, 1, [worker], pinned);
        var runner = new PaperExperimentWorkerRunner(
            source, registry, new PlatformSupplementalExperimentEvidenceProvider(source));

        var analysis = await runner.AnalyzeAsync(worker, group, group.Assignments.Single(), confirmationClose);
        var premature = await new PlatformSupplementalExperimentEvidenceProvider(source)
            .EvaluateAsync(worker.UserId, family, setup, Pin(Now), executionSeries:
                Series("ETH/EUR", CandleInterval.OneHour, Now));
        var stale = await new PlatformSupplementalExperimentEvidenceProvider(source)
            .EvaluateAsync(worker.UserId, family, setup, Pin(Now.AddHours(4)), executionSeries:
                Series("ETH/EUR", CandleInterval.OneHour, Now.AddHours(4)));
        var substituted = await new PlatformSupplementalExperimentEvidenceProvider(source)
            .EvaluateAsync(worker.UserId, family, setup, pinned, executionSeries:
                Series("ETH/EUR", CandleInterval.OneHour, Now.AddHours(2)));

        Assert.True(analysis.Outcome == ExperimentAnalysisOutcome.Analyzed, analysis.Reason);
        Assert.True(analysis.Value > 0m, analysis.Reason);
        Assert.Equal(CandleInterval.OneHour, analysis.Evidence!.Interval);
        Assert.Equal(confirmationClose, analysis.Evidence.AsOfUtc);
        Assert.Equal(confirmationClose, analysis.Evidence.CloseTimeUtc);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, premature!.Outcome);
        Assert.Contains("strictly after", premature.Reason, StringComparison.Ordinal);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, stale!.Outcome);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, substituted!.Outcome);
        Assert.Contains("pinned admission", substituted.Reason, StringComparison.Ordinal);

        var evidence = analysis.Evidence;
        var decision = await new ExperimentDecisionPolicy(
            new InMemoryExperimentDecisionLedger(), new FakeTimeProvider(confirmationClose.AddSeconds(30)))
            .DecideAsync(worker, group, group.Assignments.Single(), analysis,
                new ExperimentWorkerPortfolioSnapshot(worker.UserId, worker.Id, 0m, evidence.AsOfUtc),
                new ExperimentClosedCandleIdentity(evidence.Symbol, evidence.Interval,
                    evidence.OpenTimeUtc, evidence.CloseTimeUtc, evidence.AsOfUtc));
        Assert.Equal(ExperimentProposalAction.Open, decision.Proposal.Action);
    }

    private sealed class PinnedRegimeCandleSource(
        IReadOnlyDictionary<(string, CandleInterval), ExperimentCandleSeries> series)
        : IExperimentCandleSeriesSource
    {
        public Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
            ExperimentCandleSeriesRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(series.TryGetValue((request.Symbol, request.Interval), out var result)
                ? ExperimentCandleSeriesResult.Available(result)
                : ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.NoData));
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
                ("XBT/EUR", 100m), ("ETH/EUR", 90m), ("SOL/EUR", 80m),
                ("XRP/EUR", 70m), ("TRX/EUR", 60m), ("DOGE/EUR", 50m), ("ADA/EUR", 40m)
            }
            .ToDictionary(pair => pair.Item1, pair =>
            {
                var candles = Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory).Select(index =>
                {
                    var open = Now.AddDays(-ApprovedConsensusStrategyProfiles.RequiredHistory + index);
                    var close = pair.Item2 + index;
                    return new Candle(pair.Item1, CandleInterval.OneDay, open, open.AddDays(1),
                        close - 1m, close + 1m, close - 2m, close, 1m, true, false);
                }).ToArray();
                return ExperimentCandleSeriesResult.Available(
                    new ExperimentCandleSeries(pair.Item1, CandleInterval.OneDay, Now, candles));
            });

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class MultiIntervalCandleSource(
        CandleInterval? unavailable = null,
        DateTimeOffset? primaryCloseUtc = null,
        bool changeLatest = false)
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
            var candles = Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                .Select(index =>
                {
                    var openTime = closeUtc.AddTicks(duration.Ticks * (index - ApprovedConsensusStrategyProfiles.RequiredHistory));
                    var price = 100m + index;
                    var isLatest = index == ApprovedConsensusStrategyProfiles.RequiredHistory - 1;
                    return new Candle(
                        "BTC/USD",
                        request.Interval,
                        openTime,
                        openTime.Add(duration),
                        price,
                        price + 1m,
                        index == ApprovedConsensusStrategyProfiles.RequiredHistory - 2
                            ? price - 15m
                            : isLatest && changeLatest ? price - 2m : price - 1m,
                        isLatest && changeLatest ? price - 2m : price + 0.5m,
                        isLatest ? 2m : 1m,
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
        var profile = ResolveTimeframes(definition.FamilyId, signalInterval);
        var permittedIntervals = allowedIntervals
            ?? new[] { profile.Regime, profile.Signal, profile.Execution }.Distinct().ToArray();
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
                ApprovedConsensusStrategyProfiles.RequiredHistory, 1m, 1m, 1m, TimeSpan.FromHours(1),
                permittedIntervals,
                new[] { TradingProductType.Spot },
                new[] { StrategyApprovalMode.Paper },
                profile));
        var actor = approval.CreatedBy;
        approval = approval.TransitionTo(StrategyApprovalState.UnderReview, actor, Now)
            .TransitionTo(StrategyApprovalState.Approved, actor, Now, actor);
        var fingerprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var dataset = new HistoricalDataset("dataset", "research", worker.MarketSymbol, "1H", Now.AddDays(-30), Now.AddHours(-1), ApprovedConsensusStrategyProfiles.RequiredHistory, fingerprint, "v1", Now);
        var evidence = new StrategyResearchEvidence(
            new ResearchEvidenceProvenance("research", fingerprint, Now),
            new StrategyApprovalEvidence(InstrumentId, AssetClass.Cryptocurrency, ApprovedConsensusStrategyProfiles.RequiredHistory, 10m, 0.1m, 0.01m, Now),
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
        var candles = Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory).Select(index =>
        {
            var open = Now.AddHours(-ApprovedConsensusStrategyProfiles.RequiredHistory + index);
            var price = 100m + index;
            var low = index == ApprovedConsensusStrategyProfiles.RequiredHistory - 2 ? price - 15m : price - 1m;
            return new Candle("BTC/USD", CandleInterval.OneHour, open, open.AddHours(1), price, price + 1m, low, price + 0.5m, index == ApprovedConsensusStrategyProfiles.RequiredHistory - 1 ? 2m : 1m, true, false);
        }).ToArray();
        return ExperimentCandleSeriesResult.Available(new ExperimentCandleSeries("BTC/USD", CandleInterval.OneHour, Now, candles));
    }

    private static StrategyTimeframeConfiguration ResolveTimeframes(string familyId, CandleInterval signalInterval)
    {
        var profile = ApprovedConsensusStrategyProfiles.For(familyId)
            .FirstOrDefault(candidate => candidate.Signal == signalInterval)
            ?? ApprovedConsensusStrategyProfiles.For(familyId)[0];
        return new StrategyTimeframeConfiguration(profile.Regime, profile.Signal, profile.Execution);
    }
}
