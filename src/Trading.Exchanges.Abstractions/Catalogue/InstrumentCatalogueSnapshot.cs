namespace Trading.Exchanges.Abstractions.Catalogue;

/// <summary>
/// The result of one catalogue synchronisation attempt.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is either complete or it is not usable for absence decisions.
/// This distinction matters: if a truncated or partially failed response were
/// treated as authoritative, every instrument missing from it would be
/// suspended at once, halting the platform on a transport fault rather than
/// on a real exchange change.
/// </para>
/// <para>
/// Use <see cref="CreateComplete"/> only when the entire catalogue was
/// retrieved successfully. Otherwise use <see cref="CreatePartial"/>, which
/// still allows observed instruments to be refreshed but never allows an
/// unobserved instrument to be treated as absent.
/// </para>
/// </remarks>
public sealed class InstrumentCatalogueSnapshot
{
    private InstrumentCatalogueSnapshot(
        string exchangeName,
        IReadOnlyList<InstrumentCatalogueEntry> entries,
        DateTimeOffset retrievedAtUtc,
        bool isComplete,
        string? incompletenessReason)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (string.IsNullOrWhiteSpace(exchangeName))
        {
            throw new ArgumentException("Exchange name is required.", nameof(exchangeName));
        }

        var duplicate = entries
            .GroupBy(entry => entry.ExchangeSymbol, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"The catalogue contains duplicate symbol '{duplicate.Key}'.", nameof(entries));
        }

        ExchangeName = exchangeName.Trim();
        Entries = entries;
        RetrievedAtUtc = retrievedAtUtc;
        IsComplete = isComplete;
        IncompletenessReason = incompletenessReason;
    }

    public string ExchangeName { get; }

    public IReadOnlyList<InstrumentCatalogueEntry> Entries { get; }

    public DateTimeOffset RetrievedAtUtc { get; }

    /// <summary>
    /// True only when the whole catalogue was retrieved. Absence may be acted
    /// on only when this is true.
    /// </summary>
    public bool IsComplete { get; }

    public string? IncompletenessReason { get; }

    public static InstrumentCatalogueSnapshot CreateComplete(
        string exchangeName,
        IReadOnlyList<InstrumentCatalogueEntry> entries,
        DateTimeOffset retrievedAtUtc) =>
        new(exchangeName, entries, retrievedAtUtc, isComplete: true, incompletenessReason: null);

    public static InstrumentCatalogueSnapshot CreatePartial(
        string exchangeName,
        IReadOnlyList<InstrumentCatalogueEntry> entries,
        DateTimeOffset retrievedAtUtc,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "A partial snapshot must state why it is incomplete.", nameof(reason));
        }

        return new InstrumentCatalogueSnapshot(
            exchangeName, entries, retrievedAtUtc, isComplete: false, incompletenessReason: reason.Trim());
    }

    public InstrumentCatalogueEntry? Find(string exchangeSymbol) =>
        Entries.FirstOrDefault(entry =>
            string.Equals(entry.ExchangeSymbol, exchangeSymbol, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads an exchange's instrument catalogue. Public market data only: this
/// port never carries credentials and must never be given a signed client.
/// </summary>
public interface IInstrumentCatalogueSource
{
    string ExchangeName { get; }

    Task<InstrumentCatalogueSnapshot> GetCatalogueAsync(CancellationToken cancellationToken);
}
