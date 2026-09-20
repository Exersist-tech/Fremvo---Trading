using System.Text.Json.Serialization;

namespace Trading.Exchanges.Kraken.Catalogue;

/// <summary>
/// Wire shape of Kraken's <c>/0/public/AssetPairs</c> response.
/// </summary>
/// <remarks>
/// Internal on purpose. Kraken's field names, its <c>XXBT</c> style asset
/// codes, and its response envelope must never appear outside this connector.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by System.Text.Json during deserialization.")]
internal sealed class KrakenAssetPairsResponseDto
{
    /// <summary>
    /// Kraken reports failures in an error array while still returning HTTP
    /// 200, so a non-empty array must be treated as a failed call.
    /// </summary>
    [JsonPropertyName("error")]
    public List<string>? Error { get; set; }

    [JsonPropertyName("result")]
    public Dictionary<string, KrakenAssetPairDto>? Result { get; set; }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by System.Text.Json during deserialization.")]
internal sealed class KrakenAssetPairDto
{
    /// <summary>
    /// The alternate, human-facing pair name such as <c>XBTUSD</c>.
    /// </summary>
    [JsonPropertyName("altname")]
    public string? AltName { get; set; }

    /// <summary>
    /// The websocket name such as <c>XBT/USD</c>.
    /// </summary>
    [JsonPropertyName("wsname")]
    public string? WsName { get; set; }

    [JsonPropertyName("base")]
    public string? Base { get; set; }

    [JsonPropertyName("quote")]
    public string? Quote { get; set; }

    [JsonPropertyName("aclass_base")]
    public string? BaseAssetClass { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Price tick, string encoded.
    /// </summary>
    [JsonPropertyName("tick_size")]
    public string? TickSize { get; set; }

    /// <summary>
    /// Number of decimals allowed on a quantity. The quantity step is ten
    /// raised to the negative of this value.
    /// </summary>
    [JsonPropertyName("lot_decimals")]
    public int? LotDecimals { get; set; }

    /// <summary>
    /// Minimum order quantity, string encoded.
    /// </summary>
    [JsonPropertyName("ordermin")]
    public string? OrderMin { get; set; }

    /// <summary>
    /// Minimum order cost in the quote asset, string encoded.
    /// </summary>
    [JsonPropertyName("costmin")]
    public string? CostMin { get; set; }

    /// <summary>
    /// Leverage levels offered to buyers. A non-empty list means the pair is
    /// marginable on Kraken; it never means margin is enabled for a user.
    /// </summary>
    [JsonPropertyName("leverage_buy")]
    public List<decimal>? LeverageBuy { get; set; }

    [JsonPropertyName("leverage_sell")]
    public List<decimal>? LeverageSell { get; set; }
}
