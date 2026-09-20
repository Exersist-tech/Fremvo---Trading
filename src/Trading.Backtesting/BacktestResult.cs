using System.Collections.ObjectModel;

namespace Trading.Backtesting;

public sealed class BacktestResult
{
    public BacktestResult(
        string strategyId,
        string symbol,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        decimal initialCapital,
        decimal finalPortfolioValue,
        decimal netPnL,
        decimal totalFees,
        decimal totalSlippage,
        int tradeCount)
        : this(strategyId, symbol, fromUtc, toUtc, initialCapital, finalPortfolioValue, netPnL,
            totalFees, totalSlippage, tradeCount, null, Array.Empty<BacktestEvent>(), Array.Empty<BacktestEquitySnapshot>())
    {
    }

    public BacktestResult(
        string strategyId,
        string symbol,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        decimal initialCapital,
        decimal finalPortfolioValue,
        decimal netPnL,
        decimal totalFees,
        decimal totalSlippage,
        int tradeCount,
        string? datasetVersionIdentity,
        IReadOnlyList<BacktestEvent> events,
        IReadOnlyList<BacktestEquitySnapshot> equitySnapshots)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("Backtest end time must be after start time.", nameof(toUtc));
        }

        if (initialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapital), "Initial capital must be positive.");
        }

        StrategyId = strategyId.Trim();
        Symbol = symbol.Trim();
        FromUtc = fromUtc;
        ToUtc = toUtc;
        InitialCapital = initialCapital;
        FinalPortfolioValue = finalPortfolioValue;
        NetPnL = netPnL;
        TotalFees = totalFees;
        TotalSlippage = totalSlippage;
        TradeCount = tradeCount;
        DatasetVersionIdentity = datasetVersionIdentity;
        Events = new ReadOnlyCollection<BacktestEvent>((events ?? throw new ArgumentNullException(nameof(events))).ToArray());
        EquitySnapshots = new ReadOnlyCollection<BacktestEquitySnapshot>((equitySnapshots ?? throw new ArgumentNullException(nameof(equitySnapshots))).ToArray());
    }

    public string StrategyId { get; }

    public string Symbol { get; }

    public DateTimeOffset FromUtc { get; }

    public DateTimeOffset ToUtc { get; }

    public decimal InitialCapital { get; }

    public decimal FinalPortfolioValue { get; }

    public decimal NetPnL { get; }

    public decimal TotalFees { get; }

    public decimal TotalSlippage { get; }

    public int TradeCount { get; }

    public string? DatasetVersionIdentity { get; }

    public IReadOnlyList<BacktestEvent> Events { get; }

    public IReadOnlyList<BacktestEquitySnapshot> EquitySnapshots { get; }
}
