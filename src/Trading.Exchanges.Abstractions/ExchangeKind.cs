namespace Trading.Exchanges.Abstractions;

/// <summary>
/// The exchanges the platform can connect to.
/// </summary>
/// <remarks>
/// The numeric values are persisted. Value 1 previously denoted a different
/// venue, but no database migration was ever generated and no row exists that
/// could carry the old meaning, so reassigning it here is safe. From this
/// point on, new venues must append a new value rather than reuse one.
/// Kraken is the first supported venue; the enum exists so the rest of the
/// platform stays exchange-neutral rather than assuming a single connector.
/// </remarks>
public enum ExchangeKind
{
    None = 0,
    Kraken = 1
}
