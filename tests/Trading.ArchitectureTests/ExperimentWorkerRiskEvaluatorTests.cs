using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class ExperimentWorkerRiskEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IncreasingFillRequiresEveryExplicitGateBeforeModelOrWorkerLedgerChanges()
    {
        var harness = new Harness();
        var absent = harness.Evaluate(null);
        Assert.False(absent.IsAllowed);

        foreach (var mutate in new Func<ExperimentWorkerRiskEvaluationRequest, ExperimentWorkerRiskEvaluationRequest>[]
        {
            request => request with { Approval = request.Approval with { IsApproved = false } },
            request => request with { Approval = request.Approval with { UserId = Guid.NewGuid() } },
            request => request with { Exposure = request.Exposure with { UserId = Guid.NewGuid() } },
            request => request with { Eligibility = new InstrumentEligibility(Guid.NewGuid()) },
            request => request with { EligibilityScope = request.EligibilityScope with { Purpose = EligibilityPurpose.FuturesTest } },
            request => request with { MarketDataAsOfUtc = Now.AddMinutes(-6) },
            request => request with { AccountDataAsOfUtc = Now.AddTicks(1) },
            request => request with { Mode = new TradingModeFlags { ReduceOnlyMode = true } },
            request => request with { MarketHalted = true },
            request => request with { AccountHalted = true },
            request => request with { StrategyHalted = true },
            request => request with { EmergencyStop = true },
            request => request with { HaltSwitch = new HaltSwitch(HaltScope.Global, true) },
            request => request with { Budget = request.Budget with { MaxOrderQuantity = 0.5m } },
        })
        {
            var request = mutate(harness.Request());
            var record = harness.Model.EvaluateAndApply(harness.Worker, request.ProposedFill, harness.Evaluator, request);
            Assert.Equal(ExperimentPaperFillStatus.Rejected, record.Status);
            Assert.Empty(harness.Worker.Ledger);
            Assert.Empty(harness.Model.Ledger);
        }
    }

    [Fact]
    public void PlatformCeilingWinsOverWorkerAndUserOrStrategySettingsWithoutClamping()
    {
        var harness = new Harness();
        var request = harness.Request() with
        {
            Budget = new ExperimentWorkerRiskBudget(10m, 10_000m, 5),
            PlatformLimits = new RiskLimitHierarchy(50m, 1m, userMaxExposure: 9_000m, strategyMaxExposure: 8_000m),
            ProposedFill = harness.Request().ProposedFill with { RequestedQuantity = 2m }
        };
        var result = harness.Evaluator.Evaluate(request);

        Assert.False(result.IsAllowed);
        Assert.Contains("maximum position", result.Reason, StringComparison.OrdinalIgnoreCase);
        var record = harness.Model.EvaluateAndApply(harness.Worker, request.ProposedFill, harness.Evaluator, request);
        Assert.Equal(ExperimentPaperFillStatus.Rejected, record.Status);
        Assert.Equal(2m, record.NormalizedRequestedQuantity);
        Assert.Empty(harness.Worker.Ledger);
        Assert.Empty(harness.Model.Ledger);
    }

    [Fact]
    public void WorkerBudgetAndAdditionLimitAreStricterAndOwnerIsolated()
    {
        var harness = new Harness();
        var budgetDenied = harness.Evaluator.Evaluate(harness.Request() with
        {
            Budget = new ExperimentWorkerRiskBudget(2m, 99m, 5),
            ProposedFill = harness.Request().ProposedFill with { RequestedQuantity = 3m }
        });
        Assert.False(budgetDenied.IsAllowed);

        harness.Worker.ApplyPaperTrade(1m, 10m, 0m, "buy", Now);
        harness.Worker.RecordFavorablePaperMark(11m);
        var additionsDenied = harness.Evaluator.Evaluate(harness.Request() with
        {
            Budget = new ExperimentWorkerRiskBudget(10m, 10_000m, 0),
            Exposure = new(harness.Worker.UserId, harness.Worker.Id, 1m, 10m, 0),
        });
        Assert.False(additionsDenied.IsAllowed);
        var foreign = harness.Evaluator.Evaluate(harness.Request() with
        {
            Approval = new(Guid.NewGuid(), harness.Worker.Id, ExperimentResearchGroup.A, 1, harness.Worker.StrategyId, true)
        });
        Assert.False(foreign.IsAllowed);
    }

    [Fact]
    public void ReductionsUseSafetyExitPolicyAndNeedNoIncreasingEligibilityOrFreshAccountEvidence()
    {
        var harness = new Harness();
        harness.Worker.ApplyPaperTrade(1m, 10m, 0m, "buy", Now);
        var request = harness.Request() with
        {
            ProposedAction = ExperimentProposalAction.Close,
            ProposedFill = harness.Request().ProposedFill with { Direction = TradeDirection.Sell, RequestedQuantity = 1m },
            Eligibility = new InstrumentEligibility(Guid.NewGuid()),
            MarketDataAsOfUtc = Now,
            AccountDataAsOfUtc = Now.AddHours(-1),
            Approval = new(Guid.Empty, Guid.Empty, ExperimentResearchGroup.A, 0, "", false)
        };

        var record = harness.Model.EvaluateAndApply(harness.Worker, request.ProposedFill, harness.Evaluator, request);

        Assert.Equal(ExperimentPaperFillStatus.Filled, record.Status);
        Assert.Equal(0m, harness.Worker.PositionQuantity);
        Assert.Equal(2, harness.Worker.Ledger.Count);
    }

    private sealed class Harness
    {
        public ExperimentWorker Worker { get; } = CreateWorker();
        public ExperimentPaperFillModel Model { get; } = new(new FeeModel(0m, 0m, 0m), SlippageModel.Zero, new ExchangeFilter(1m, 0.1m, 0.01m, 0.1m));
        public ExperimentWorkerRiskEvaluator Evaluator { get; } = new(new RiskEngine(timeProvider: new FixedTimeProvider(Now)));

        public ExperimentWorkerRiskEvaluationRequest Request()
        {
            var instrumentId = Guid.NewGuid();
            var scope = new EligibilityScope(EligibilityPurpose.Paper, CandleInterval.OneHour, TradingProductType.Spot);
            var eligibility = new InstrumentEligibility(instrumentId);
            eligibility.Grant(scope with { Purpose = EligibilityPurpose.Research }, Now, Now, TimeSpan.FromMinutes(5));
            eligibility.Grant(scope with { Purpose = EligibilityPurpose.Backtest }, Now, Now, TimeSpan.FromMinutes(5));
            eligibility.Grant(scope, Now, Now, TimeSpan.FromMinutes(5));
            var fill = new ExperimentPaperFillRequest("BTC/USD", TradeDirection.Buy, 1m, 10m, false, 1m, Now);
            return new(Worker, new(Worker.UserId, Worker.Id, ExperimentResearchGroup.A, 1, Worker.StrategyId, true),
                new(10m, 100m, 5), new(Worker.UserId, Worker.Id, Worker.PositionQuantity, Worker.PositionQuantity * 10m, Worker.AdditionCount),
                instrumentId, eligibility, scope, TimeSpan.FromMinutes(5), new StalenessPolicy(TimeSpan.FromMinutes(5)),
                Now, Now, Now, new TradingModeFlags(), null, false, false, false, false,
                new RiskLimitHierarchy(100m, 10m), ExperimentProposalAction.Open, fill);
        }

        public ExperimentWorkerRiskEvaluation Evaluate(ExperimentWorkerRiskEvaluationRequest? request) => Evaluator.Evaluate(request);

        private static ExperimentWorker CreateWorker()
        {
            var worker = new ExperimentWorker(Guid.NewGuid(), Guid.NewGuid(), "risk-worker", "research", "BTC/USD", 1_000m, Now, 1);
            worker.Start();
            return worker;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
