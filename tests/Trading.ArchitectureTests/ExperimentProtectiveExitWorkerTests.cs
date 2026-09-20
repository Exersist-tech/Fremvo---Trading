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
    public async Task TriggerRoutesOnlyOnceThroughPaperPipelineAndHonorsGapAndBothLevelRule()
    {
        var harness = new Harness(Candle(Now.AddMinutes(-1), 280m, 320m, 270m));
        var first = await harness.Orchestrator.EvaluateOwnerAsync(harness.User);
        var second = await harness.Orchestrator.EvaluateOwnerAsync(harness.User);

        Assert.True(Assert.Single(first).Submitted);
        Assert.Contains(second, result => result.Reason.Contains("already claimed", StringComparison.Ordinal));
        var fill = Assert.Single(harness.Adapter.Ledger);
        Assert.Equal(TradeDirection.Sell, fill.Direction);
        Assert.Equal(280m, fill.Price); // Both levels takes the stop; opening below it honors the gap.
        Assert.Single(await harness.Commands.ListForUserAsync(harness.User));
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
    }

    [Fact]
    public async Task DisabledScheduleDoesNothingAndOwnerFaultDoesNotStopOtherOwners()
    {
        var ownerOne = Guid.NewGuid();
        var ownerTwo = Guid.NewGuid();
        var evaluator = new RecordingOwnerEvaluator(ownerOne);
        using var disabled = new ProtectiveExitWorker(NullLogger<ProtectiveExitWorker>.Instance, evaluator,
            Options.Create(new ExperimentProtectiveExitWorkerOptions { Enabled = false, EnabledUserIds = { ownerOne } }),
            new FixedTimeProvider(Now));
        Assert.Equal(0, await disabled.RunTickAsync());
        Assert.Empty(evaluator.Owners);

        using var enabled = new ProtectiveExitWorker(NullLogger<ProtectiveExitWorker>.Instance, evaluator,
            Options.Create(new ExperimentProtectiveExitWorkerOptions { Enabled = true, EnabledUserIds = { ownerOne, ownerTwo } }),
            new FixedTimeProvider(Now));
        await enabled.RunTickAsync();
        Assert.Contains(ownerOne, evaluator.Owners);
        Assert.Contains(ownerTwo, evaluator.Owners);
    }

    [Fact]
    public async Task CancellationPropagatesAndHostHasNoLiveOrFuturesDependencies()
    {
        var evaluator = new RecordingOwnerEvaluator();
        using var worker = new ProtectiveExitWorker(NullLogger<ProtectiveExitWorker>.Instance, evaluator,
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

    private static Candle Candle(DateTimeOffset close, decimal open, decimal high, decimal low) =>
        new("BTC/USD", CandleInterval.OneMinute, close.AddMinutes(-1), close, open, high, low, open, 1m, true, false);

    private sealed class Harness
    {
        public Guid User { get; } = Guid.NewGuid();
        public PaperExecutionAdapter Adapter { get; } = new();
        public InMemoryExecutionCommandRepository Commands { get; } = new();
        public ExperimentProtectiveExitOrchestrator Orchestrator { get; }

        public Harness(Candle candle) : this(ExperimentCandleSeriesResult.Available(
            new ExperimentCandleSeries("BTC/USD", CandleInterval.OneMinute, Now, [candle]))) { }

        public Harness(ExperimentCandleSeriesResult candles)
        {
            var worker = Guid.NewGuid();
            var position = new ExperimentProtectiveExitPosition(User, worker, Guid.NewGuid(), "BTC/USD", 2m, 300m,
                Now.AddHours(-1), 290m, 310m);
            var decisions = new InMemoryExperimentDecisionLedger();
            var pipeline = new TradePipeline(new InMemoryMarketEventRepository(), new InMemoryStrategyDecisionRepository(),
                new InMemoryTradeIntentRepository(), new InMemoryRiskEvaluationRepository(), Commands,
                new InMemoryPortfolioUpdateRepository(), new RecordingAudit(), new RiskEngine(), new InMemoryTradingHaltState(),
                new OrderIdempotencyGuard(), new TradePipelineOptions { MaxNotional = 100_000m, MaxPositionSize = 100m },
                timeProvider: new FixedTimeProvider(Now));
            var paper = new PaperExperimentTradeOrchestrator(decisions, new InMemoryExperimentPaperExecutionLedger(), pipeline, Adapter);
            Orchestrator = new ExperimentProtectiveExitOrchestrator(
                new FixedPositions(position), new FixedCandles(candles), new InMemoryExperimentProtectiveExitLedger(),
                decisions, paper, new FixedTimeProvider(Now));
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

    private sealed class RecordingAudit : IAuditEventWriter
    {
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingOwnerEvaluator(Guid? faultingOwner = null) : IExperimentProtectiveExitOwnerEvaluator
    {
        public List<Guid> Owners { get; } = [];
        public Task<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>> EvaluateOwnerAsync(Guid userId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Owners.Add(userId);
            if (userId == faultingOwner)
                throw new InvalidOperationException("isolated");
            return Task.FromResult<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>>([]);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
