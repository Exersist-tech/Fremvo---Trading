using System.Globalization;
using System.Text.Json;
using Trading.Exchanges.Abstractions.Catalogue;

namespace Trading.Exchanges.Kraken.Catalogue;

/// <summary>
/// Translates a Kraken <c>AssetPairs</c> document into neutral catalogue
/// entries.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place that understands Kraken's wire format. It performs
/// no I/O, so it is fully testable from a captured document, and it holds no
/// credentials: <c>AssetPairs</c> is public market data.
/// </para>
/// <para>
/// Parsing is strict. A pair whose identity fields are missing is skipped
/// rather than guessed at, and an unparsable trading rule is left
/// <c>null</c> rather than defaulted, so the filters gate fails closed.
/// </para>
/// </remarks>
public static class KrakenAssetPairMapper
{
    public const string ExchangeName = "Kraken";

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Maps a raw Kraken AssetPairs JSON document.
    /// </summary>
    /// <exception cref="FormatException">
    /// The document could not be read as a Kraken AssetPairs response, or
    /// Kraken reported an error. A malformed document is an error, never an
    /// empty catalogue, because an empty catalogue would look like a mass
    /// delisting and would suspend the whole universe.
    /// </exception>
    public static IReadOnlyList<InstrumentCatalogueEntry> MapDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        KrakenAssetPairsResponseDto? document;
        try
        {
            document = JsonSerializer.Deserialize<KrakenAssetPairsResponseDto>(json, s_options);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The Kraken AssetPairs document could not be parsed.", exception);
        }

        if (document is null)
        {
            throw new FormatException("The Kraken AssetPairs document was empty.");
        }

        // Kraken answers with HTTP 200 even when the call failed, reporting
        // the failure only in this array. Ignoring it would turn an outage
        // into an apparently successful, empty catalogue.
        if (document.Error is { Count: > 0 })
        {
            throw new FormatException(
                $"Kraken reported an error for AssetPairs: {string.Join("; ", document.Error)}");
        }

        if (document.Result is null)
        {
            throw new FormatException("The Kraken AssetPairs document contained no result element.");
        }

        var entries = new List<InstrumentCatalogueEntry>(document.Result.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in document.Result)
        {
            var entry = MapPair(pair.Key, pair.Value);
            if (entry is not null && seen.Add(entry.ExchangeSymbol))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static InstrumentCatalogueEntry? MapPair(string key, KrakenAssetPairDto? pair)
    {
        if (pair is null || string.IsNullOrWhiteSpace(pair.Status))
        {
            // Without a status we cannot tell whether the pair is tradable.
            return null;
        }

        // Kraken's dictionary key is its internal pair id. The altname is the
        // stable, human-facing symbol and is preferred; the key is only a
        // fallback so a pair is never dropped purely for lacking an altname.
        var symbol = FirstNonEmpty(pair.AltName, key);
        if (symbol is null)
        {
            return null;
        }

        var (baseAsset, quoteAsset) = ResolveAssets(pair);
        if (baseAsset is null || quoteAsset is null)
        {
            // Identity is missing. Skipping is the safe outcome: an instrument
            // we cannot identify must not enter the universe.
            return null;
        }

        return new InstrumentCatalogueEntry(
            symbol,
            baseAsset,
            quoteAsset,
            MapStatus(pair.Status),
            pair.Status,
            MapCapabilities(pair),
            ParseDecimal(pair.TickSize),
            MapQuantityStep(pair.LotDecimals),
            ParseDecimal(pair.OrderMin),
            ParseDecimal(pair.CostMin),

            // Kraken's AssetPairs response carries no listing date. Leaving
            // this null makes the listing age unknown, which fails the
            // new-listing gate rather than granting a false age.
            onboardUtc: null);
    }

    /// <summary>
    /// Translates Kraken's status vocabulary into the neutral vocabulary.
    /// </summary>
    /// <remarks>
    /// Only <c>online</c> is fully tradable. Every restricted status maps to
    /// its own neutral value, and anything unrecognised maps to
    /// <see cref="CatalogueTradingStatus.Unknown"/>, so a status Kraken adds
    /// in future can never be silently treated as permission to trade.
    /// </remarks>
    private static CatalogueTradingStatus MapStatus(string status) =>
        status.Trim().ToUpperInvariant() switch
        {
            "ONLINE" => CatalogueTradingStatus.Trading,
            "LIMIT_ONLY" => CatalogueTradingStatus.LimitOnly,
            "POST_ONLY" => CatalogueTradingStatus.PostOnly,
            "REDUCE_ONLY" => CatalogueTradingStatus.ReduceOnly,
            "CANCEL_ONLY" => CatalogueTradingStatus.CancelOnly,
            "MAINTENANCE" => CatalogueTradingStatus.Halted,
            "DELISTED" => CatalogueTradingStatus.Delisted,
            _ => CatalogueTradingStatus.Unknown
        };

    /// <summary>
    /// Derives neutral capability names.
    /// </summary>
    /// <remarks>
    /// Every listed Kraken asset pair is spot tradable. Margin is reported
    /// separately through the leverage lists and is recorded as its own
    /// capability, never as a flag on spot, so enabling spot can never enable
    /// leverage as a side effect.
    /// </remarks>
    private static List<string> MapCapabilities(KrakenAssetPairDto pair)
    {
        var capabilities = new List<string> { NeutralCapabilities.Spot };

        if (pair.LeverageBuy is { Count: > 0 } || pair.LeverageSell is { Count: > 0 })
        {
            capabilities.Add(NeutralCapabilities.Margin);
        }

        return capabilities;
    }

    /// <summary>
    /// Resolves the base and quote assets, preferring the websocket name.
    /// </summary>
    /// <remarks>
    /// Kraken's <c>base</c> and <c>quote</c> fields use its internal codes,
    /// where Bitcoin is <c>XXBT</c> and US dollars are <c>ZUSD</c>. The
    /// websocket name carries the display codes, which are what the rest of
    /// the platform and the user expect to see.
    /// </remarks>
    private static (string? Base, string? Quote) ResolveAssets(KrakenAssetPairDto pair)
    {
        if (!string.IsNullOrWhiteSpace(pair.WsName))
        {
            var parts = pair.WsName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                return (parts[0], parts[1]);
            }
        }

        var baseAsset = NormalizeAssetCode(pair.Base);
        var quoteAsset = NormalizeAssetCode(pair.Quote);

        return (baseAsset, quoteAsset);
    }

    /// <summary>
    /// Strips Kraken's historical single-character asset class prefix.
    /// </summary>
    /// <remarks>
    /// Kraken prefixes some legacy codes with <c>X</c> for crypto and
    /// <c>Z</c> for fiat, giving <c>XXBT</c> and <c>ZUSD</c>. The prefix is
    /// only removed from four-character codes, because shorter codes such as
    /// <c>XRP</c> and <c>ZEC</c> are real assets whose first letter must be
    /// kept.
    /// </remarks>
    private static string? NormalizeAssetCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim().ToUpperInvariant();

        if (trimmed.Length == 4 && (trimmed[0] == 'X' || trimmed[0] == 'Z'))
        {
            return trimmed[1..];
        }

        return trimmed;
    }

    /// <summary>
    /// Converts Kraken's quantity decimal count into a step size.
    /// </summary>
    /// <remarks>
    /// Eight decimals becomes a step of 0.00000001. The value is computed with
    /// <see cref="decimal"/> arithmetic rather than a floating point power, so
    /// the step is exact and an order quantity can never be rounded to a
    /// materially different size.
    /// </remarks>
    private static decimal? MapQuantityStep(int? lotDecimals)
    {
        if (lotDecimals is null or < 0 or > 28)
        {
            return null;
        }

        var step = 1m;
        for (var index = 0; index < lotDecimals.Value; index++)
        {
            step /= 10m;
        }

        return step;
    }

    /// <summary>
    /// Parses a Kraken string-encoded number to <see cref="decimal"/> using
    /// the invariant culture, so a host locale using a comma decimal
    /// separator can never change the meaning of a trading rule.
    /// </summary>
    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        // A zero-encoded rule is not a usable trading rule. Treating it as
        // "no minimum" would let an order through that the exchange rejects.
        return parsed > 0m ? parsed : null;
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim();
}

/// <summary>
/// Neutral capability names emitted by this connector.
/// </summary>
internal static class NeutralCapabilities
{
    internal const string Spot = "SPOT";

    internal const string Margin = "MARGIN";
}
