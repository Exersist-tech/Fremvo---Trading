namespace Trading.Domain.Scanner;

/// <summary>
/// Read-only evidence that a symbol met a scan's configured criteria.
/// A result is informational only; it is not advice, a signal, or permission
/// to create an order.
/// </summary>
public sealed class ScanResult : IEquatable<ScanResult>
{
    private readonly IReadOnlyList<ScanCriterionKind> _matchedCriteria;

    public ScanResult(
        Guid ownerId,
        Guid scanRequestId,
        Guid scanRunId,
        string symbol,
        int rank,
        decimal score,
        IEnumerable<ScanCriterionKind> matchedCriteria,
        DateTimeOffset evidenceAsOfUtc,
        DateTimeOffset evaluatedAtUtc)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("Scan result owner id is required.", nameof(ownerId));
        }

        if (scanRequestId == Guid.Empty || scanRunId == Guid.Empty)
        {
            throw new ArgumentException("Scan request and run ids are required.", nameof(scanRequestId));
        }

        if (string.IsNullOrWhiteSpace(symbol) || symbol.Trim().Length > 32)
        {
            throw new ArgumentException("A scan result symbol up to 32 characters is required.", nameof(symbol));
        }

        if (rank < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), "A result rank must be positive.");
        }

        if (score < 0m || score > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(score), "A result score must be normalized between zero and one.");
        }

        if (evidenceAsOfUtc == default || evaluatedAtUtc == default)
        {
            throw new ArgumentException("Evidence and evaluation timestamps are required.", nameof(evidenceAsOfUtc));
        }

        var normalizedEvidenceAsOfUtc = evidenceAsOfUtc.ToUniversalTime();
        var normalizedEvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
        if (normalizedEvidenceAsOfUtc > normalizedEvaluatedAtUtc)
        {
            throw new ArgumentException("Evidence cannot be newer than its evaluation.", nameof(evidenceAsOfUtc));
        }

        ArgumentNullException.ThrowIfNull(matchedCriteria);
        var normalizedMatchedCriteria = matchedCriteria
            .Select(kind =>
            {
                if (!Enum.IsDefined(kind) || kind == ScanCriterionKind.None)
                {
                    throw new ArgumentOutOfRangeException(nameof(matchedCriteria), "A supported criterion kind is required.");
                }

                return kind;
            })
            .Distinct()
            .OrderBy(kind => kind)
            .ToArray();

        if (normalizedMatchedCriteria.Length == 0)
        {
            throw new ArgumentException("A result must identify matched criteria.", nameof(matchedCriteria));
        }

        OwnerId = ownerId;
        ScanRequestId = scanRequestId;
        ScanRunId = scanRunId;
        Symbol = symbol.Trim().ToUpperInvariant();
        Rank = rank;
        Score = score;
        EvidenceAsOfUtc = normalizedEvidenceAsOfUtc;
        EvaluatedAtUtc = normalizedEvaluatedAtUtc;
        _matchedCriteria = Array.AsReadOnly(normalizedMatchedCriteria);
    }

    public Guid OwnerId { get; }

    public Guid ScanRequestId { get; }

    public Guid ScanRunId { get; }

    public string Symbol { get; }

    /// <summary>One is the best rank; lower ranks sort first.</summary>
    public int Rank { get; }

    /// <summary>A normalized relevance score, not an expected return or recommendation.</summary>
    public decimal Score { get; }

    public IReadOnlyList<ScanCriterionKind> MatchedCriteria => _matchedCriteria;

    public DateTimeOffset EvidenceAsOfUtc { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }

    public bool Equals(ScanResult? other) =>
        other is not null &&
        OwnerId == other.OwnerId &&
        ScanRequestId == other.ScanRequestId &&
        ScanRunId == other.ScanRunId &&
        string.Equals(Symbol, other.Symbol, StringComparison.Ordinal) &&
        Rank == other.Rank &&
        Score == other.Score &&
        EvidenceAsOfUtc == other.EvidenceAsOfUtc &&
        EvaluatedAtUtc == other.EvaluatedAtUtc &&
        MatchedCriteria.SequenceEqual(other.MatchedCriteria);

    public override bool Equals(object? obj) => Equals(obj as ScanResult);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(OwnerId);
        hash.Add(ScanRequestId);
        hash.Add(ScanRunId);
        hash.Add(Symbol, StringComparer.Ordinal);
        hash.Add(Rank);
        hash.Add(Score);
        hash.Add(EvidenceAsOfUtc);
        hash.Add(EvaluatedAtUtc);
        foreach (var criterion in MatchedCriteria)
        {
            hash.Add(criterion);
        }

        return hash.ToHashCode();
    }
}
