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
        FeeModel feeModel,
        SlippageModel slippageModel,
        ExchangeFilter exchangeFilter,
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

        StrategyId = strategyId.Trim();
        Symbol = symbol.Trim();
        FromUtc = fromUtc;
        ToUtc = toUtc;
        WarmupCandles = warmupCandles;
        InitialCapital = initialCapital;
        FeeModel = feeModel ?? throw new ArgumentNullException(nameof(feeModel));
        SlippageModel = slippageModel ?? throw new ArgumentNullException(nameof(slippageModel));
        ExchangeFilter = exchangeFilter ?? throw new ArgumentNullException(nameof(exchangeFilter));
        DatasetName = datasetName?.Trim();
    }

    [Obsolete("Use the constructor with explicit fee, slippage, and exchange-filter models.")]
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
        : this(
            strategyId, symbol, fromUtc, toUtc, warmupCandles, initialCapital,
            new FeeModel(commissionRate, commissionRate, 0m),
            new SlippageModel(0m, slippageRate),
            // Compatibility only: new backtests must explicitly name venue rules.
            new ExchangeFilter(0m, 0m, 0.0000000000000000000000000001m, 0.0000000000000000000000000001m),
            datasetName)
    {
    }

    public string StrategyId { get; }

    public string Symbol { get; }

    public DateTimeOffset FromUtc { get; }

    public DateTimeOffset ToUtc { get; }

    public int WarmupCandles { get; }

    public decimal InitialCapital { get; }

    public FeeModel FeeModel { get; }

    public SlippageModel SlippageModel { get; }

    public ExchangeFilter ExchangeFilter { get; }

    [Obsolete("Use FeeModel instead.")]
    public decimal CommissionRate => FeeModel.TakerFeeRate;

    [Obsolete("Use SlippageModel instead.")]
    public decimal SlippageRate => SlippageModel.PercentSlippage;

    public string? DatasetName { get; }
}
