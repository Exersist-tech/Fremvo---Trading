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
}
