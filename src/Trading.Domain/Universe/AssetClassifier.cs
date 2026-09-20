namespace Trading.Domain.Universe;

/// <summary>
/// Classifies an asset code so that stablecoins, fiat, tokenized equity, and
/// leveraged tokens can be permanently excluded from the research universe.
/// </summary>
/// <remarks>
/// This classifier is deliberately conservative. An asset it does not
/// recognise is <see cref="AssetClass.Unknown"/>, which is excluded, rather
/// than being optimistically assumed to be a cryptocurrency. Adding an asset
/// to the known-cryptocurrency set is a explicit, reviewable decision.
/// </remarks>
public sealed class AssetClassifier
{
    private static readonly string[] LeveragedTokenSuffixes =
    {
        "UP", "DOWN", "BULL", "BEAR", "3L", "3S", "5L", "5S"
    };

    private readonly HashSet<string> _stablecoins;
    private readonly HashSet<string> _fiat;
    private readonly HashSet<string> _tokenizedEquities;
    private readonly HashSet<string> _cryptocurrencies;

    public AssetClassifier(
        IEnumerable<string> cryptocurrencies,
        IEnumerable<string>? stablecoins = null,
        IEnumerable<string>? fiat = null,
        IEnumerable<string>? tokenizedEquities = null)
    {
        ArgumentNullException.ThrowIfNull(cryptocurrencies);

        _cryptocurrencies = Normalize(cryptocurrencies);
        _stablecoins = stablecoins is null ? DefaultStablecoins() : Normalize(stablecoins);
        _fiat = fiat is null ? DefaultFiat() : Normalize(fiat);
        _tokenizedEquities = tokenizedEquities is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : Normalize(tokenizedEquities);

        // A code cannot be both an approved cryptocurrency and an excluded
        // class. Silently preferring one would hide a configuration mistake.
        foreach (var code in _cryptocurrencies)
        {
            if (_stablecoins.Contains(code) || _fiat.Contains(code) || _tokenizedEquities.Contains(code))
            {
                throw new ArgumentException(
                    $"Asset '{code}' is listed both as a cryptocurrency and as an excluded class.",
                    nameof(cryptocurrencies));
            }
        }
    }

    /// <summary>
    /// The stablecoin codes this classifier recognises.
    /// </summary>
    public IReadOnlyCollection<string> Stablecoins => _stablecoins;

    public AssetClass Classify(string assetCode)
    {
        if (string.IsNullOrWhiteSpace(assetCode))
        {
            throw new ArgumentException("Asset code is required.", nameof(assetCode));
        }

        var code = assetCode.Trim().ToUpperInvariant();

        if (_stablecoins.Contains(code))
        {
            return AssetClass.Stablecoin;
        }

        if (_fiat.Contains(code))
        {
            return AssetClass.Fiat;
        }

        if (_tokenizedEquities.Contains(code))
        {
            return AssetClass.TokenizedEquity;
        }

        if (IsLeveragedTokenCode(code))
        {
            return AssetClass.LeveragedToken;
        }

        return _cryptocurrencies.Contains(code)
            ? AssetClass.Cryptocurrency
            : AssetClass.Unknown;
    }

    /// <summary>
    /// True when the code looks like a leveraged or rebalanced basket token.
    /// Checked before the known-cryptocurrency lookup so that, for example,
    /// a "BTCUP" token is never treated as BTC.
    /// </summary>
    private bool IsLeveragedTokenCode(string code)
    {
        foreach (var suffix in LeveragedTokenSuffixes)
        {
            if (code.Length <= suffix.Length || !code.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            // Only a leveraged token when the remaining stem is itself a
            // recognised asset, so a genuine coin whose ticker merely ends in
            // these letters is not misclassified.
            var stem = code[..^suffix.Length];
            if (_cryptocurrencies.Contains(stem))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> Normalize(IEnumerable<string> codes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("Asset codes cannot be blank.", nameof(codes));
            }

            set.Add(code.Trim().ToUpperInvariant());
        }

        return set;
    }

    private static HashSet<string> DefaultStablecoins() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            "USDT", "USDC", "BUSD", "FDUSD", "TUSD", "DAI", "USDP", "GUSD",
            "USDD", "PYUSD", "EURI", "AEUR", "USD1", "LUSD", "FRAX", "SUSD"
        };

    private static HashSet<string> DefaultFiat() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            "USD", "EUR", "GBP", "NOK", "SEK", "DKK", "CHF", "JPY", "AUD",
            "CAD", "NZD", "PLN", "TRY", "BRL", "ARS", "ZAR", "MXN", "RUB",
            "UAH", "INR", "IDR", "NGN", "KRW", "SGD", "HKD", "CNY", "AED"
        };
}
