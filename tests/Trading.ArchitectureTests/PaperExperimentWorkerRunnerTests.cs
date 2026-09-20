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
    public async Task EveryPhase5BFamilyResolvesToItsOwnInputBlockedAdapterWithoutChangingWorkerState()
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

            Assert.Equal(ExperimentAnalysisOutcome.Blocked, first.Outcome);
            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.Reason, second.Reason);
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

        Assert.Equal(10, registry.Definitions.Count);
        Assert.DoesNotContain(typeof(ApprovedExperimentStrategyRegistry).GetMethods(), method =>
            method.Name.Contains("Register", StringComparison.OrdinalIgnoreCase)
            || method.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
    }

    [Fact]
    public async Task AttestedObservationsMapDeterministicallyAndNeverAddExposure()
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
        var existingExposure = await new ExperimentDecisionPolicy(new InMemoryExperimentDecisionLedger(), new FakeTimeProvider(Now)).DecideAsync(worker, configuration, configuration.Assignments.Single(), analysis,
            snapshot with { PositionQuantity = 1m }, identity);

        Assert.Equal(ExperimentProposalAction.Neutral, first.Proposal.Action);
        Assert.Equal(first, repeated);
        Assert.Equal(ExperimentProposalAction.Neutral, existingExposure.Proposal.Action);
        Assert.DoesNotContain(Enum.GetNames<ExperimentProposalAction>(), action => action.Equals("Add", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(worker.Ledger);
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
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
        bool acceptedGate = true)
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
                new[] { CandleInterval.OneHour },
                new[] { TradingProductType.Spot },
                new[] { StrategyApprovalMode.Paper },
                new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.OneHour, CandleInterval.OneHour)));
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
