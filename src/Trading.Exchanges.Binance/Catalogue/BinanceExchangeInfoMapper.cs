using System.Globalization;
using System.Text.Json;
using Trading.Exchanges.Abstractions.Catalogue;

namespace Trading.Exchanges.Binance.Catalogue;

/// <summary>
/// Translates a Binance <c>exchangeInfo</c> document into neutral catalogue
/// entries.
/// </summary>
/// <remarks>
/// This is the only place that understands Binance's wire format. It performs
/// no I/O, so it is fully testable from a captured document, and it holds no
/// credentials: <c>exchangeInfo</c> is public market data.
///
/// Parsing is strict. A symbol whose identity fields are missing is skipped
/// rather than guessed at, and an unparsable trading rule is left
/// <c>null</c> rather than defaulted, so the filters gate fails closed.
/// </remarks>
public static class BinanceExchangeInfoMapper
{
    public const string ExchangeName = "Binance";

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Maps a raw Binance exchangeInfo JSON document.
    /// </summary>
    /// <exception cref="FormatException">
    /// The document could not be read as a Binance exchangeInfo response.
    /// A malformed document is an error, never an empty catalogue, because an
    /// empty catalogue would look like a mass delisting.
    /// </exception>
    public static IReadOnlyList<InstrumentCatalogueEntry> MapDocument(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        BinanceExchangeInfoDto? document;
        try
        {
            document = JsonSerializer.Deserialize<BinanceExchangeInfoDto>(json, s_options);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The Binance exchangeInfo document could not be parsed.", exception);
        }

        if (document?.Symbols is null)
        {
            throw new FormatException("The Binance exchangeInfo document contained no symbols element.");
        }

        var entries = new List<InstrumentCatalogueEntry>(document.Symbols.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in document.Symbols)
        {
            var entry = MapSymbol(symbol);
            if (entry is not null && seen.Add(entry.ExchangeSymbol))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static InstrumentCatalogueEntry? MapSymbol(BinanceSymbolDto symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Symbol) ||
            string.IsNullOrWhiteSpace(symbol.BaseAsset) ||
            string.IsNullOrWhiteSpace(symbol.QuoteAsset) ||
            string.IsNullOrWhiteSpace(symbol.Status))
        {
            // Identity or status is missing. Skipping is the safe outcome:
            // an instrument we cannot identify must not enter the universe.
            return null;
        }

        decimal? tickSize = null;
        decimal? stepSize = null;
        decimal? minimumQuantity = null;
        decimal? minimumNotional = null;

        foreach (var filter in symbol.Filters ?? Array.Empty<BinanceSymbolFilterDto>())
        {
            switch (filter.FilterType?.ToUpperInvariant())
            {
                case "PRICE_FILTER":
                    tickSize = ParseDecimal(filter.TickSize);
                    break;
                case "LOT_SIZE":
                    stepSize = ParseDecimal(filter.StepSize);
                    minimumQuantity = ParseDecimal(filter.MinQty);
                    break;
                case "MIN_NOTIONAL":
                case "NOTIONAL":
                    minimumNotional = ParseDecimal(filter.MinNotional) ?? ParseDecimal(filter.Notional);
                    break;
                default:
                    break;
            }
        }

        return new InstrumentCatalogueEntry(
            symbol.Symbol,
            symbol.BaseAsset,
            symbol.QuoteAsset,
            symbol.Status,
            MapPermissions(symbol),
            tickSize,
            stepSize,
            minimumQuantity,
            minimumNotional,
            MapOnboardDate(symbol.OnboardDate));
    }

    /// <summary>
    /// Binance reports permissions either as a flat list or, on newer
    /// responses, as permission sets. Both are flattened; neither is assumed.
    /// </summary>
    private static List<string> MapPermissions(BinanceSymbolDto symbol)
    {
        var permissions = new List<string>();

        if (symbol.Permissions is not null)
        {
            permissions.AddRange(symbol.Permissions);
        }

        if (symbol.PermissionSets is not null)
        {
            foreach (var set in symbol.PermissionSets)
            {
                if (set is not null)
                {
                    permissions.AddRange(set);
                }
            }
        }

        return permissions;
    }

    private static DateTimeOffset? MapOnboardDate(long? onboardDate)
    {
        if (onboardDate is null or <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(onboardDate.Value).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            // An out-of-range onboarding date means the listing age is unknown,
            // which fails the listing-age gate rather than granting a false age.
            return null;
        }
    }

    /// <summary>
    /// Parses a Binance string-encoded number to <see cref="decimal"/> using
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

        // Binance encodes "no limit" as 0. That is not a usable trading rule.
        return parsed > 0m ? parsed : null;
    }
}
