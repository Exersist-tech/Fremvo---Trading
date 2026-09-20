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
    public const string Version = "2026-09-20.kraken-spot-usd-50";

    public const string ExchangeName = "Kraken";

    /// <summary>
    /// Kraken's deepest spot liquidity is quoted in US dollars, so the seed
    /// is USD quoted. This is a data-selection choice for research only and
    /// is unrelated to a user's reporting currency, which each user chooses
    /// for themselves.
    /// </summary>
    public const string QuoteAsset = "USD";

    /// <summary>
    /// Base assets use Kraken's display codes, so Bitcoin is XBT and
    /// Dogecoin is XDG. Translating these to any other convention is the
    /// connector's job, never this list's.
    /// </summary>
    private static readonly string[] BaseAssets =
    {
        "XBT",   "ETH",   "SOL",   "XRP",   "ADA",
        "XDG",   "LINK",  "AVAX",  "LTC",   "DOT",
        "TRX",   "XLM",   "BCH",   "ATOM",  "UNI",
        "NEAR",  "FIL",   "ETC",   "AAVE",  "ALGO",
        "XTZ",   "XMR",   "ZEC",   "DASH",  "ICP",
        "INJ",   "SUI",   "APT",   "ARB",   "OP",
        "TIA",   "SEI",   "RENDER", "GRT",  "MANA",
        "SAND",  "AXS",   "CRV",   "COMP",  "SNX",
        "LDO",   "PEPE",  "SHIB",  "WIF",   "BONK",
        "ONDO",  "ENA",   "JUP",   "PYTH",  "TAO"
    };

    /// <summary>
    /// The base asset codes in the seed. These are treated as known
    /// cryptocurrencies by the classifier; every other asset remains
    /// <see cref="AssetClass.Unknown"/> and therefore excluded.
    /// </summary>
    public static IReadOnlyList<string> SeedBaseAssets => BaseAssets;

    /// <summary>
    /// The seeded symbols, for example "XBTUSD".
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
