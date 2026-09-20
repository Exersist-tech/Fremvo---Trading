namespace Trading.MarketData;

/// <summary>
/// A pair a venue will accept orders on, together with the filters the venue
/// enforces on those orders.
/// </summary>
/// <remarks>
/// <para>
/// The filters are carried here rather than looked up at submission time
/// because an order that violates a tick, step or minimum is rejected by the
/// venue. Knowing the rule before submitting is what lets the platform refuse
/// an invalid order explicitly instead of discovering it as a venue error.
/// </para>
/// <para>
/// This type is exchange-neutral. It names no venue and carries no venue
/// identifier beyond <see cref="Symbol"/>, which is the string that venue
/// accepts.
/// </para>
/// </remarks>
public sealed class TradablePair
{
    public TradablePair(
        string symbol,
        string displayName,
        string baseAsset,
        string quoteAsset,
        bool isActive,
        decimal minimumQuantity,
        decimal quantityStep,
        decimal priceTick)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            throw new ArgumentException("Base asset is required.", nameof(baseAsset));
        }

        if (string.IsNullOrWhiteSpace(quoteAsset))
        {
            throw new ArgumentException("Quote asset is required.", nameof(quoteAsset));
        }

        // A non-positive filter is not a permissive filter, it is an unknown
        // one. Accepting zero here would let a caller believe any size or
        // price is valid and submit an order the venue will reject.
        if (minimumQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumQuantity), "Minimum quantity must be positive.");
        }

        if (quantityStep <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityStep), "Quantity step must be positive.");
        }

        if (priceTick <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(priceTick), "Price tick must be positive.");
        }

        Symbol = symbol.Trim();
        DisplayName = displayName.Trim();
        BaseAsset = baseAsset.Trim();
        QuoteAsset = quoteAsset.Trim();
        IsActive = isActive;
        MinimumQuantity = minimumQuantity;
        QuantityStep = quantityStep;
        PriceTick = priceTick;
    }

    /// <summary>The identifier the venue accepts in a request.</summary>
    public string Symbol { get; }

    /// <summary>A human-readable name, for example <c>XBT/USD</c>.</summary>
    public string DisplayName { get; }

    public string BaseAsset { get; }

    public string QuoteAsset { get; }

    /// <summary>
    /// Whether the venue is currently accepting orders on this pair. A pair
    /// that is not active still has price history, so it may be charted, but
    /// an order must not be sent to it.
    /// </summary>
    public bool IsActive { get; }

    /// <summary>The smallest order the venue will accept.</summary>
    public decimal MinimumQuantity { get; }

    /// <summary>Order quantity must be a whole multiple of this.</summary>
    public decimal QuantityStep { get; }

    /// <summary>Order price must be a whole multiple of this.</summary>
    public decimal PriceTick { get; }
}

/// <summary>
/// Supplies the pairs a venue will trade. Exchange-neutral: an implementation
/// lives in a connector, and no caller learns which venue answered.
/// </summary>
public interface ITradablePairSource
{
    /// <summary>
    /// Lists every pair the venue publishes, ordered by display name.
    /// </summary>
    /// <exception cref="MarketDataSourceException">
    /// The venue could not be reached or refused the request. This is never
    /// reported as an empty list, because "the venue trades nothing" and "the
    /// request failed" lead to opposite decisions.
    /// </exception>
    Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default);
}
