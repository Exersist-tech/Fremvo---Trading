using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class ExperimentPaperFillModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PartialBuyUsesAdverseDecimalPriceAndReconcilesFeeInclusiveCost()
    {
        var worker = Worker(100m);
        var model = Model(new FeeModel(0m, 0.01m, 0m), new SlippageModel(0.03m));

        var fill = Apply(model, worker, Request(TradeDirection.Buy, 2.37m, 10m, 0.5m));

        Assert.Equal(ExperimentPaperFillStatus.PartiallyFilled, fill.Status);
        Assert.Equal(2.3m, fill.NormalizedRequestedQuantity);
        Assert.Equal(1.1m, fill.FilledQuantity);
        Assert.Equal(10.03m, fill.ExecutionPrice);
        Assert.Equal(11.033m, fill.Notional);
        Assert.Equal(0.11033m, fill.Fee);
        Assert.Equal(0.033m, fill.Slippage);
        Assert.True(fill.QuantityAdjusted);
        Assert.Equal(88.85667m, worker.CashBalance);
        Assert.Equal(1.1m, worker.PositionQuantity);
        Assert.Equal(10.1303m, worker.AverageEntryPrice);
        Assert.Equal(0m, worker.RealizedProfitAndLoss);
        Assert.Single(worker.Ledger);
        Assert.Single(model.Ledger);
    }

    [Fact]
    public void FullSellUsesAdverseDecimalPriceAndReconcilesCashAndProfit()
    {
        var worker = Worker(100m);
        var model = Model(new FeeModel(0m, 0.01m, 0m), new SlippageModel(0.03m));
        Apply(model, worker, Request(TradeDirection.Buy, 2m, 10m, 1m));

        var fill = Apply(model, worker, Request(TradeDirection.Sell, 1m, 12m, 1m));

        Assert.Equal(ExperimentPaperFillStatus.Filled, fill.Status);
        Assert.Equal(11.97m, fill.ExecutionPrice);
        Assert.Equal(11.97m, fill.Notional);
        Assert.Equal(0.1197m, fill.Fee);
        Assert.Equal(0.03m, fill.Slippage);
        Assert.Equal(91.5897m, worker.CashBalance);
        Assert.Equal(1m, worker.PositionQuantity);
        Assert.Equal(10.1303m, worker.AverageEntryPrice);
        Assert.Equal(1.72m, worker.RealizedProfitAndLoss);
        Assert.Equal(Now, worker.Ledger.Last().OccurredAtUtc);
    }

    [Fact]
    public void TickStepAndMinimumFiltersRecordConservativeAdjustmentOrRejectionWithoutMutation()
    {
        var worker = Worker(100m);
        var model = Model(new FeeModel(0m, 0m, 0m), new SlippageModel(0.006m), minNotional: 10m);

        var adjusted = Apply(model, worker, Request(TradeDirection.Buy, 1.09m, 10m, 1m));
        var beforeCash = worker.CashBalance;
        var rejected = Apply(model, worker, Request(TradeDirection.Buy, 0.19m, 10m, 1m));

        Assert.Equal(ExperimentPaperFillStatus.Filled, adjusted.Status);
        Assert.Equal(1m, adjusted.FilledQuantity);
        Assert.Equal(10.01m, adjusted.ExecutionPrice);
        Assert.True(adjusted.QuantityAdjusted);
        Assert.True(adjusted.PriceAdjusted);
        Assert.Equal(ExperimentPaperFillStatus.Rejected, rejected.Status);
        Assert.Contains("minimum", rejected.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeCash, worker.CashBalance);
        Assert.Equal(1m, worker.PositionQuantity);
        Assert.Equal(2, model.Ledger.Count);
    }

    [Fact]
    public void DeterministicReplayAndInsufficientLiquidityProduceIdenticalResultsOrExplicitRejection()
    {
        var first = Model(new FeeModel(0.001m, 0.002m, 0m), new SlippageModel(0.005m, 0.001m));
        var second = Model(new FeeModel(0.001m, 0.002m, 0m), new SlippageModel(0.005m, 0.001m));
        var request = Request(TradeDirection.Sell, 2m, 10m, 0.75m);

        Assert.Equal(first.Evaluate(request), second.Evaluate(request));
        var missingLiquidity = first.Evaluate(Request(TradeDirection.Buy, 1m, 10m, null));

        Assert.Equal(ExperimentPaperFillStatus.Rejected, missingLiquidity.Status);
        Assert.Contains("explicit liquidity", missingLiquidity.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectedCashAndUnsafeAdditionLeaveWorkerStateUnchanged()
    {
        var worker = Worker(20m);
        var model = Model(new FeeModel(0m, 0.01m, 0m), SlippageModel.Zero);
        Apply(model, worker, Request(TradeDirection.Buy, 1m, 10m, 1m));
        var cash = worker.CashBalance;
        var quantity = worker.PositionQuantity;
        var cost = worker.AverageEntryPrice;

        var unaffordable = Apply(model, worker, Request(TradeDirection.Buy, 2m, 10m, 1m));
        var unsafeAdd = Apply(model, worker, Request(TradeDirection.Buy, 0.5m, 10m, 1m));

        Assert.Equal(ExperimentPaperFillStatus.Rejected, unaffordable.Status);
        Assert.Equal(ExperimentPaperFillStatus.Rejected, unsafeAdd.Status);
        Assert.Equal(cash, worker.CashBalance);
        Assert.Equal(quantity, worker.PositionQuantity);
        Assert.Equal(cost, worker.AverageEntryPrice);
        Assert.Single(worker.Ledger);
    }

    [Fact]
    public void FavorableAdditionUsesFeeInclusiveWeightedCostBasis()
    {
        var worker = Worker(100m);
        var model = Model(new FeeModel(0m, 0.01m, 0m), SlippageModel.Zero);
        Apply(model, worker, Request(TradeDirection.Buy, 1m, 10m, 1m));
        worker.RecordFavorablePaperMark(11m);

        var addition = Apply(model, worker, Request(TradeDirection.Buy, 1m, 12m, 1m));

        Assert.Equal(ExperimentPaperFillStatus.Filled, addition.Status);
        Assert.Equal(2m, worker.PositionQuantity);
        Assert.Equal(11.11m, worker.AverageEntryPrice);
        Assert.Equal(77.78m, worker.CashBalance);
        Assert.Equal(2, worker.Ledger.Count);
    }

    private static ExperimentPaperFillModel Model(FeeModel fees, SlippageModel slippage, decimal minNotional = 1m) =>
        new(fees, slippage, new ExchangeFilter(minNotional, 0.1m, 0.01m, 0.1m));

    private static ExperimentPaperFillRequest Request(TradeDirection direction, decimal quantity, decimal price, decimal? ratio) =>
        new("BTC/USD", direction, quantity, price, false, ratio, Now);

    private static ExperimentPaperFillRecord Apply(
        ExperimentPaperFillModel model, ExperimentWorker worker, ExperimentPaperFillRequest request)
    {
        var instrumentId = Guid.NewGuid();
        var scope = new EligibilityScope(EligibilityPurpose.Paper, CandleInterval.OneHour, TradingProductType.Spot);
        var eligibility = new InstrumentEligibility(instrumentId);
        eligibility.Grant(scope with { Purpose = EligibilityPurpose.Research }, Now, Now, TimeSpan.FromMinutes(5));
        eligibility.Grant(scope with { Purpose = EligibilityPurpose.Backtest }, Now, Now, TimeSpan.FromMinutes(5));
        eligibility.Grant(scope, Now, Now, TimeSpan.FromMinutes(5));
        var action = request.Direction == TradeDirection.Buy ? ExperimentProposalAction.Open : ExperimentProposalAction.Reduce;
        var evaluation = new ExperimentWorkerRiskEvaluationRequest(
            worker,
            new(worker.UserId, worker.Id, ExperimentResearchGroup.A, 1, worker.StrategyId, true),
            new(100m, 100_000m, 5),
            new(worker.UserId, worker.Id, worker.PositionQuantity, worker.PositionQuantity * request.ReferencePrice, worker.AdditionCount),
            instrumentId, eligibility, scope, TimeSpan.FromMinutes(5), new StalenessPolicy(TimeSpan.FromMinutes(5)),
            Now, Now, Now, new TradingModeFlags(), null, false, false, false, false,
            new RiskLimitHierarchy(100_000m, 100m), action, request);
        return model.EvaluateAndApply(worker, request, new ExperimentWorkerRiskEvaluator(new RiskEngine()), evaluation);
    }

    private static ExperimentWorker Worker(decimal cash)
    {
        var worker = new ExperimentWorker(Guid.NewGuid(), Guid.NewGuid(), "fill-worker", "research", "BTC/USD", cash, Now, 1);
        worker.Start();
        return worker;
    }
}
