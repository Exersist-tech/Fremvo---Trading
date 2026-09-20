namespace Trading.Domain.Universe;

/// <summary>
/// Lifecycle state of an instrument. Every instrument begins as
/// <see cref="Tracked"/>. The higher eligibility grants are additive and are
/// modelled separately; this enum records the instrument's own lifecycle
/// position.
/// </summary>
public enum InstrumentState
{
    /// <summary>
    /// Known to the platform. Catalogue data and candles may be collected.
    /// No research, no backtest, no paper trading, no live trading.
    /// </summary>
    Tracked = 0,

    /// <summary>
    /// Enough history and data health for exploratory analysis and the scanner.
    /// </summary>
    ResearchEligible = 1,

    /// <summary>
    /// Complete, gap-free required history; exchange filters loaded.
    /// </summary>
    BacktestEligible = 2,

    /// <summary>
    /// Backtest-eligible plus live data health and current liquidity evidence.
    /// </summary>
    PaperEligible = 3,

    /// <summary>
    /// Paper-eligible plus an execution path proven against recorded
    /// exchange responses.
    /// </summary>
    /// <remarks>
    /// Kraken publishes no public Spot sandbox, so this grant does not mean
    /// "proven on a testnet". It permits only minimum-size orders under the
    /// proving notional ceiling, on a real account with real funds, and is
    /// therefore subject to every live-trading safety control.
    /// </remarks>
    SpotProvingEligible = 4,

    /// <summary>
    /// Requires an explicit, audited administrator approval. Never automatic.
    /// </summary>
    SpotLiveEligible = 5,

    FuturesTestEligible = 6,

    /// <summary>
    /// Requires an explicit, audited administrator approval, and only after
    /// Spot live trading is stable. Never automatic.
    /// </summary>
    FuturesLiveEligible = 7,

    /// <summary>
    /// Blocked by the exchange, by data health, by liquidity failure, or by an
    /// operator. New exposure is forbidden; validated reduction is permitted.
    /// </summary>
    Suspended = 8,

    /// <summary>
    /// Delisted or permanently withdrawn. Records are preserved.
    /// </summary>
    Removed = 9
}

/// <summary>
/// Why an instrument is barred from the research universe.
/// </summary>
public enum InstrumentExclusionReason
{
    None = 0,
    NotClassified = 1,
    StablecoinPair = 2,
    TokenizedEquity = 3,
    Fiat = 4,
    LeveragedToken = 5,
    QuoteAssetNotAllowed = 6,
    NotPresentOnExchange = 7,
    ExchangeStatusNotTrading = 8,
    SpotPermissionMissing = 9,
    Removed = 10
}
