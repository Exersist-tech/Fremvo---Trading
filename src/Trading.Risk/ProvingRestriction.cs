namespace Trading.Risk;

/// <summary>
/// The restrictions that apply while an exchange account is proving its live
/// execution route.
/// </summary>
/// <remarks>
/// <para>
/// Because Kraken publishes no public Spot sandbox, the only way to prove that
/// placement, reconciliation and fill handling work against the real venue is
/// to place real orders. This type bounds what those orders may be: a small
/// notional ceiling, and a short list of instruments chosen for liquidity.
/// </para>
/// <para>
/// It is evaluated by the risk engine rather than by the execution adapter on
/// purpose. The adapter is the one component that can reach the exchange, and
/// a limit enforced only there would have no second opinion behind it.
/// </para>
/// <para>
/// The restriction is stated in exchange-neutral terms. It carries no account
/// entity and no stage enum from the connector layer, so the risk engine keeps
/// no dependency on exchange abstractions.
/// </para>
/// </remarks>
public sealed class ProvingRestriction
{
    public ProvingRestriction(
        string symbol,
        decimal orderNotional,
        decimal? notionalCeiling,
        IReadOnlyCollection<string> permittedSymbols)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (orderNotional <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderNotional), "Order notional must be positive.");
        }

        Symbol = symbol.Trim();
        OrderNotional = orderNotional;
        NotionalCeiling = notionalCeiling;
        PermittedSymbols = permittedSymbols ?? throw new ArgumentNullException(nameof(permittedSymbols));
    }

    public string Symbol { get; }

    /// <summary>The notional this order would carry, in the quote currency.</summary>
    public decimal OrderNotional { get; }

    /// <summary>
    /// The ceiling recorded on the account. Null means no ceiling has been
    /// set, which blocks proving orders entirely rather than permitting any
    /// size.
    /// </summary>
    public decimal? NotionalCeiling { get; }

    /// <summary>
    /// The instruments a proving account may trade. An empty set blocks every
    /// instrument, which is the correct reading of "nothing has been approved".
    /// </summary>
    public IReadOnlyCollection<string> PermittedSymbols { get; }

    /// <summary>
    /// The reason this order must be blocked, or null when it is within the
    /// proving bounds.
    /// </summary>
    public string? Violation()
    {
        if (NotionalCeiling is not { } ceiling)
        {
            return "No proving notional ceiling has been set for this account, so no real order may be placed.";
        }

        if (OrderNotional > ceiling)
        {
            return "The order exceeds the proving notional ceiling for this account.";
        }

        if (PermittedSymbols.Count == 0)
        {
            return "No instruments have been approved for proving on this account.";
        }

        return PermittedSymbols.Contains(Symbol, StringComparer.OrdinalIgnoreCase)
            ? null
            : "This instrument is not approved for proving on this account.";
    }
}
