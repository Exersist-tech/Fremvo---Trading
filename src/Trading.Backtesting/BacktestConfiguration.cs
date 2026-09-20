namespace Trading.Backtesting;

public sealed class BacktestConfiguration
{
    public BacktestConfiguration(
        string strategyId,
        string symbol,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int warmupCandles,
        decimal initialCapital,
        decimal commissionRate,
        decimal slippageRate,
        string? datasetName = null)
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
            throw new ArgumentException("Backtest end time must be after the start time.", nameof(toUtc));
        }

        if (warmupCandles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupCandles), "Warmup candles cannot be negative.");
        }

        if (initialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapital), "Initial capital must be positive.");
        }

        if (commissionRate < 0m || slippageRate < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(commissionRate), "Commission and slippage rates cannot be negative.");
        }

        StrategyId = strategyId.Trim();
        Symbol = symbol.Trim();
        FromUtc = fromUtc;
        ToUtc = toUtc;
        WarmupCandles = warmupCandles;
        InitialCapital = initialCapital;
        CommissionRate = commissionRate;
        SlippageRate = slippageRate;
        DatasetName = datasetName?.Trim();
    }

    public string StrategyId { get; }

    public string Symbol { get; }

    public DateTimeOffset FromUtc { get; }

    public DateTimeOffset ToUtc { get; }

    public int WarmupCandles { get; }

    public decimal InitialCapital { get; }

    public decimal CommissionRate { get; }

    public decimal SlippageRate { get; }

    public string? DatasetName { get; }
}
