using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Trading.Exchanges.Binance.Catalogue;

/// <summary>
/// Binance <c>/api/v3/exchangeInfo</c> wire model.
/// </summary>
/// <remarks>
/// Deliberately <c>internal</c>. Binance field names, casing, status strings,
/// and string-encoded numbers must never escape this connector; callers only
/// ever see the neutral catalogue types.
///
/// Every numeric trading rule arrives as a JSON string and is parsed to
/// <see cref="decimal"/>. It is never parsed to a binary floating-point type,
/// which could not represent a tick size such as 0.00000001 exactly.
/// </remarks>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json during deserialization.")]
internal sealed class BinanceExchangeInfoDto
{
    [JsonPropertyName("symbols")]
    public IReadOnlyList<BinanceSymbolDto>? Symbols { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json during deserialization.")]
internal sealed class BinanceSymbolDto
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("baseAsset")]
    public string? BaseAsset { get; init; }

    [JsonPropertyName("quoteAsset")]
    public string? QuoteAsset { get; init; }

    [JsonPropertyName("permissions")]
    public IReadOnlyList<string>? Permissions { get; init; }

    [JsonPropertyName("permissionSets")]
    public IReadOnlyList<IReadOnlyList<string>>? PermissionSets { get; init; }

    [JsonPropertyName("onboardDate")]
    public long? OnboardDate { get; init; }

    [JsonPropertyName("filters")]
    public IReadOnlyList<BinanceSymbolFilterDto>? Filters { get; init; }
}

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json during deserialization.")]
internal sealed class BinanceSymbolFilterDto
{
    [JsonPropertyName("filterType")]
    public string? FilterType { get; init; }

    [JsonPropertyName("tickSize")]
    public string? TickSize { get; init; }

    [JsonPropertyName("stepSize")]
    public string? StepSize { get; init; }

    [JsonPropertyName("minQty")]
    public string? MinQty { get; init; }

    [JsonPropertyName("minNotional")]
    public string? MinNotional { get; init; }

    [JsonPropertyName("notional")]
    public string? Notional { get; init; }
}
