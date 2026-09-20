using Trading.Domain.Market;

namespace Trading.Domain.Scanner;

/// <summary>
/// An owner-scoped, bounded request to screen a fixed market-data universe.
/// Creating a request grants no trading capability and makes no recommendation.
/// </summary>
public sealed class ScanRequest
{
    public const int MaximumNameLength = 200;
    public const int MaximumSymbols = 250;
    public const int MaximumResults = 250;

    private readonly IReadOnlyList<string> _symbols;
    private readonly IReadOnlyList<ScanCriterion> _criteria;

    public ScanRequest(
        Guid id,
        Guid ownerId,
        string name,
        IEnumerable<string> symbols,
        CandleInterval interval,
        IEnumerable<ScanCriterion> criteria,
        int resultLimit,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Scan request id is required.", nameof(id));
        }

        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("Scan request owner id is required.", nameof(ownerId));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
        {
            throw new ArgumentException($"A scan request name up to {MaximumNameLength} characters is required.", nameof(name));
        }

        if (!Enum.IsDefined(interval) || interval == CandleInterval.None)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "A supported candle interval is required.");
        }

        if (createdAtUtc == default)
        {
            throw new ArgumentException("A creation timestamp is required.", nameof(createdAtUtc));
        }

        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(criteria);

        var normalizedSymbols = symbols
            .Select(symbol =>
            {
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    throw new ArgumentException("Each scan symbol is required.", nameof(symbols));
                }

                var normalized = symbol.Trim().ToUpperInvariant();
                if (normalized.Length > 32)
                {
                    throw new ArgumentException("A scan symbol cannot exceed 32 characters.", nameof(symbols));
                }

                return normalized;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();

        if (normalizedSymbols.Length == 0 || normalizedSymbols.Length > MaximumSymbols)
        {
            throw new ArgumentOutOfRangeException(
                nameof(symbols),
                $"A scan must target between one and {MaximumSymbols} distinct symbols.");
        }

        var normalizedCriteria = criteria
            .Select(criterion => criterion ?? throw new ArgumentException("A scan criterion is required.", nameof(criteria)))
            .OrderBy(criterion => criterion.Kind)
            .ToArray();

        if (normalizedCriteria.Length == 0 ||
            normalizedCriteria.Select(criterion => criterion.Kind).Distinct().Count() != normalizedCriteria.Length)
        {
            throw new ArgumentException("A scan must contain one value for each distinct criterion kind.", nameof(criteria));
        }

        if (resultLimit < 1 || resultLimit > MaximumResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resultLimit),
                $"A scan result limit must be between 1 and {MaximumResults}.");
        }

        Id = id;
        OwnerId = ownerId;
        Name = name.Trim();
        Interval = interval;
        ResultLimit = resultLimit;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        _symbols = Array.AsReadOnly(normalizedSymbols);
        _criteria = Array.AsReadOnly(normalizedCriteria);
    }

    public Guid Id { get; }

    public Guid OwnerId { get; }

    public string Name { get; }

    public IReadOnlyList<string> Symbols => _symbols;

    public CandleInterval Interval { get; }

    public IReadOnlyList<ScanCriterion> Criteria => _criteria;

    public int ResultLimit { get; }

    public DateTimeOffset CreatedAtUtc { get; }
}
