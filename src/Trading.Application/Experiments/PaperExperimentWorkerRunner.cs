using Trading.Application.Pipeline;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;

namespace Trading.Application.Experiments;

/// <summary>
/// Supplies the next closed market event for one worker. Implementations serve immutable
/// historical or recorded data, which workers may share; no worker may mutate it.
/// </summary>
public interface IExperimentMarketFeed
{
    Task<MarketEvent?> TryGetNextClosedEventAsync(
        Guid workerId,
        string symbol,
        CancellationToken cancellationToken);
}

/// <summary>
/// Resolves an approved strategy template id to its executable template. Users cannot supply
/// strategy code, so an unknown id is an error rather than something to be compiled.
/// </summary>
public interface IApprovedStrategyTemplateFactory
{
    IPipelineStrategy? TryCreate(string strategyTemplateId, string parametersJson);
}

/// <summary>
/// Advances one experiment worker by a single closed market event, always in paper mode and
/// always through the full trade pipeline. The runner never reaches an exchange connector and
/// never shares state with another worker.
/// </summary>
public sealed class PaperExperimentWorkerRunner : IExperimentWorkerRunner
{
    private readonly TradePipeline _pipeline;
    private readonly IExperimentMarketFeed _marketFeed;
    private readonly IApprovedStrategyTemplateFactory _templates;
    private readonly IExecutionAdapter _paperExecutionAdapter;
    private readonly IExperimentWorkerRepository _repository;
    private readonly TimeProvider _timeProvider;

    public PaperExperimentWorkerRunner(
        TradePipeline pipeline,
        IExperimentMarketFeed marketFeed,
        IApprovedStrategyTemplateFactory templates,
        IExecutionAdapter paperExecutionAdapter,
        IExperimentWorkerRepository repository,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(marketFeed);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(paperExecutionAdapter);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _pipeline = pipeline;
        _marketFeed = marketFeed;
        _templates = templates;
        _paperExecutionAdapter = paperExecutionAdapter;
        _repository = repository;
        _timeProvider = timeProvider;
    }

    public async Task RunOnceAsync(ExperimentWorker worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worker);

        var marketEvent = await _marketFeed
            .TryGetNextClosedEventAsync(worker.Id, worker.MarketSymbol, cancellationToken)
            .ConfigureAwait(false);

        if (marketEvent is null)
        {
            // No new closed candle yet. Waiting is correct; acting on a forming candle is not.
            return;
        }

        var strategy = _templates.TryCreate(worker.StrategyId, worker.StrategyParameters)
            ?? throw new InvalidOperationException(
                "The worker references a strategy template that is not approved or no longer exists.");

        var context = new PipelineContext(
            worker.UserId,
            TradingMode.Paper,
            $"experiment-{worker.Id:N}-{marketEvent.EventTimeUtc:yyyyMMddHHmmss}");

        var portfolio = new PortfolioSnapshot(
            currentExposure: worker.PositionQuantity * marketEvent.LastPrice,
            positionQuantity: worker.PositionQuantity,
            cashBalance: worker.CashBalance,
            dailyPnL: worker.RealizedProfitAndLoss,
            openOrders: 0,
            openPositions: worker.PositionQuantity == 0m ? 0 : 1,
            lastUpdatedUtc: _timeProvider.GetUtcNow());

        var result = await _pipeline.ProcessAsync(
            marketEvent,
            context,
            strategy,
            portfolio,
            _paperExecutionAdapter,
            cancellationToken).ConfigureAwait(false);

        if (result.RequiresReconciliation)
        {
            // The paper adapter should never report an unknown outcome. If it does, the worker is
            // stopped rather than allowed to trade against an unreconciled order.
            worker.Fail("Paper execution returned an unknown status and requires reconciliation.");
            await _repository.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!result.Executed || result.PortfolioUpdate is null)
        {
            return;
        }

        var update = result.PortfolioUpdate;
        var filledQuantity = Math.Abs(update.PositionQuantityAfter - update.PositionQuantityBefore);

        if (filledQuantity == 0m)
        {
            return;
        }

        var direction = update.PositionQuantityAfter > update.PositionQuantityBefore ? "buy" : "sell";
        var executionPrice = ExecutionPriceFrom(update, filledQuantity, direction);

        worker.ApplyPaperTrade(filledQuantity, executionPrice, update.Fees, direction);
        await _repository.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recovers the average fill price from the cash movement so the worker's ledger records the
    /// price it actually paid, fees excluded.
    /// </summary>
    private static decimal ExecutionPriceFrom(PortfolioUpdate update, decimal filledQuantity, string direction)
    {
        var cashMoved = string.Equals(direction, "buy", StringComparison.Ordinal)
            ? update.CashBalanceBefore - update.CashBalanceAfter - update.Fees
            : update.CashBalanceAfter - update.CashBalanceBefore + update.Fees;

        return cashMoved / filledQuantity;
    }
}
