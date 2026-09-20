namespace Trading.Domain.Universe;

/// <summary>
/// The initial Spot research seed.
/// </summary>
/// <remarks>
/// This is a research starting point, not a permanent list and not an
/// automatically live-tradable list. Membership grants nothing: every seeded
/// instrument is created as <see cref="InstrumentState.Tracked"/> with no
/// eligibility grant, and the live exchange catalogue — not this list — is
/// the source of truth for whether a symbol exists, is trading, and has Spot
/// permission.
///
/// Several of these are recent listings with short history and unstable
/// liquidity. They are expected to fail the listing-age and liquidity gates
/// and to remain research-only for a long time. That is the intended
/// outcome, not a defect.
/// </remarks>
public static class MarketUniverseSeed
{
    /// <summary>
    /// Version tag for the seed. Changing the membership changes the tag so
    /// an eligibility decision can be traced to the seed it came from.
    /// </summary>
    public const string Version = "2026-09-20.spot-usdt-50";

    public const string ExchangeName = "Binance";

    public const string QuoteAsset = "USDT";

    private static readonly string[] BaseAssets =
    {
        "BTC",   "ETH",   "BNB",   "XRP",   "SOL",
        "DOGE",  "ADA",   "LINK",  "AVAX",  "LTC",
        "TRX",   "XLM",   "NEAR",  "UNI",   "SUI",
        "ZEC",   "FIL",   "ARB",   "APT",   "ONDO",
        "INJ",   "TAO",   "AR",    "DOT",   "TON",
        "OP",    "ETC",   "CAKE",  "CRV",   "RUNE",
        "ENA",   "WLD",   "PEPE",  "FLOKI", "STRK",
        "TIA",   "SEI",   "AXS",   "CHZ",   "ORDI",
        "BLUR",  "ZK",    "METIS", "ONE",   "PROVE",
        "BANK",  "G",     "ZAMA",  "TRUMP", "DCR"
    };

    /// <summary>
    /// The base asset codes in the seed. These are treated as known
    /// cryptocurrencies by the classifier; every other asset remains
    /// <see cref="AssetClass.Unknown"/> and therefore excluded.
    /// </summary>
    public static IReadOnlyList<string> SeedBaseAssets => BaseAssets;

    /// <summary>
    /// The seeded symbols, for example "BTCUSDT".
    /// </summary>
    public static IReadOnlyList<string> SeedSymbols =>
        BaseAssets.Select(asset => asset + QuoteAsset).ToList();

    /// <summary>
    /// A classifier that recognises the seed base assets as cryptocurrencies
    /// and applies the standard stablecoin, fiat and leveraged-token
    /// exclusions.
    /// </summary>
    public static AssetClassifier CreateClassifier() => new(BaseAssets);

    /// <summary>
    /// Materialises the seed. Every instrument is
    /// <see cref="InstrumentState.Tracked"/>, is not yet known to exist on
    /// the exchange, and holds no eligibility grant.
    /// </summary>
    /// <param name="idFactory">
    /// Supplies the instrument id for a symbol, so a caller can produce
    /// deterministic ids rather than depending on <see cref="Guid.NewGuid"/>.
    /// </param>
    public static IReadOnlyList<Instrument> Create(Func<string, Guid>? idFactory = null)
    {
        var classifier = CreateClassifier();
        var instruments = new List<Instrument>(BaseAssets.Length);

        foreach (var baseAsset in BaseAssets)
        {
            var symbol = baseAsset + QuoteAsset;
            var assetClass = classifier.Classify(baseAsset);

            instruments.Add(Instrument.CreateSeed(
                idFactory?.Invoke(symbol) ?? Guid.NewGuid(),
                ExchangeName,
                symbol,
                baseAsset,
                QuoteAsset,
                assetClass));
        }

        return instruments;
    }
}
