namespace Trading.Exchanges.Abstractions.Catalogue;

/// <summary>
/// One instrument as described by an exchange catalogue, in exchange-neutral
/// terms. Contains no exchange-specific field names, casing, or encoding.
/// </summary>
/// <remarks>
/// All numeric trading rules are <see cref="decimal"/>. A rule the exchange
/// did not supply is <c>null</c>, never a substituted default: an absent
/// minimum notional must read as "unknown" so the filters gate fails closed,
/// not as "no minimum".
/// </remarks>
public sealed record InstrumentCatalogueEntry
{
    public InstrumentCatalogueEntry(
        string exchangeSymbol,
        string baseAsset,
        string quoteAsset,
        string status,
        IReadOnlyCollection<string> permissions,
        decimal? priceTickSize = null,
        decimal? quantityStepSize = null,
        decimal? minimumQuantity = null,
        decimal? minimumNotional = null,
        DateTimeOffset? onboardUtc = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        ExchangeSymbol = Require(exchangeSymbol, nameof(exchangeSymbol)).ToUpperInvariant();
        BaseAsset = Require(baseAsset, nameof(baseAsset)).ToUpperInvariant();
        QuoteAsset = Require(quoteAsset, nameof(quoteAsset)).ToUpperInvariant();
        Status = Require(status, nameof(status)).ToUpperInvariant();

        Permissions = permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        PriceTickSize = RequirePositiveOrNull(priceTickSize, nameof(priceTickSize));
        QuantityStepSize = RequirePositiveOrNull(quantityStepSize, nameof(quantityStepSize));
        MinimumQuantity = RequirePositiveOrNull(minimumQuantity, nameof(minimumQuantity));
        MinimumNotional = RequirePositiveOrNull(minimumNotional, nameof(minimumNotional));
        OnboardUtc = onboardUtc;
    }

    public string ExchangeSymbol { get; }

    public string BaseAsset { get; }

    public string QuoteAsset { get; }

    /// <summary>
    /// The exchange's own trading status, upper-cased. Not interpreted here:
    /// the eligibility gate decides what counts as tradable.
    /// </summary>
    public string Status { get; }

    public IReadOnlyCollection<string> Permissions { get; }

    public decimal? PriceTickSize { get; }

    public decimal? QuantityStepSize { get; }

    public decimal? MinimumQuantity { get; }

    public decimal? MinimumNotional { get; }

    public DateTimeOffset? OnboardUtc { get; }

    /// <summary>
    /// True only when every trading rule needed to size and price an order is
    /// present. A partially described instrument is not usable.
    /// </summary>
    public bool HasCompleteFilters =>
        PriceTickSize is not null &&
        QuantityStepSize is not null &&
        MinimumQuantity is not null &&
        MinimumNotional is not null;

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        return value.Trim();
    }

    private static decimal? RequirePositiveOrNull(decimal? value, string parameterName)
    {
        if (value is not null && value <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                parameterName, $"{parameterName} must be positive when supplied.");
        }

        return value;
    }
}
