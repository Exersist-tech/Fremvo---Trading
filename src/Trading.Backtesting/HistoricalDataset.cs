using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Trading.Backtesting;

/// <summary>
/// Immutable, platform-owned evidence describing one reproducible historical candle dataset.
/// </summary>
public sealed class HistoricalDataset : IEquatable<HistoricalDataset>
{
    private static readonly Regex FingerprintPattern = new("^[A-F0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SupportedIntervals = new(StringComparer.Ordinal)
    {
        "1M", "5M", "10M", "15M", "30M", "1H", "4H", "1D"
    };

    public HistoricalDataset(
        string id,
        string source,
        string symbol,
        string interval,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int candleCount,
        string contentFingerprint,
        string sourceVersion,
        DateTimeOffset createdAtUtc)
    {
        Id = Required(id, nameof(id), 128);
        Source = Required(source, nameof(source), 128);
        Symbol = Required(symbol, nameof(symbol), 64);
        Interval = CanonicalizeInterval(interval);

        FromUtc = fromUtc.ToUniversalTime();
        ToUtc = toUtc.ToUniversalTime();
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        if (ToUtc < FromUtc)
        {
            throw new ArgumentException("Dataset end time must not precede start time.", nameof(toUtc));
        }

        if (!IsAligned(FromUtc, Interval) || !IsAligned(ToUtc, Interval))
        {
            throw new ArgumentException("The inclusive dataset range must align with the candle interval.", nameof(fromUtc));
        }

        if (candleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candleCount), "Candle count must be positive.");
        }

        CandleCount = candleCount;

        if (CreatedAtUtc > DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(createdAtUtc), "Dataset creation time cannot be in the future.");
        }

        if (ToUtc >= CreatedAtUtc)
        {
            throw new ArgumentException("A dataset may contain only candles closed before it was created.", nameof(toUtc));
        }

        ContentFingerprint = Required(contentFingerprint, nameof(contentFingerprint), 64).ToUpperInvariant();
        if (!FingerprintPattern.IsMatch(ContentFingerprint))
        {
            throw new ArgumentException("Content fingerprint must be a SHA-256 hexadecimal digest.", nameof(contentFingerprint));
        }

        SourceVersion = Required(sourceVersion, nameof(sourceVersion), 128);
        ContainsOnlyClosedCandles = true;
        VersionIdentity = ComputeVersionIdentity(
            Source,
            Symbol,
            Interval,
            FromUtc,
            ToUtc,
            CandleCount,
            ContentFingerprint,
            SourceVersion);
    }

    /// <summary>Caller-provided request identifier; it is not a credential or exchange identifier.</summary>
    public string Id { get; }

    /// <summary>Neutral public-data provenance label, never an exchange account or secret reference.</summary>
    public string Source { get; }

    public string Symbol { get; }

    public string Interval { get; }

    /// <summary>Inclusive first candle open timestamp, normalized to UTC.</summary>
    public DateTimeOffset FromUtc { get; }

    /// <summary>Inclusive last candle open timestamp, normalized to UTC.</summary>
    public DateTimeOffset ToUtc { get; }

    public int CandleCount { get; }

    /// <summary>SHA-256 digest of the canonical candle content, not the candle blob itself.</summary>
    public string ContentFingerprint { get; }

    public string SourceVersion { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>All datasets are platform-owned and exclusively contain closed candles.</summary>
    public bool ContainsOnlyClosedCandles { get; }

    /// <summary>Content-addressed identity for replay and repository conflict detection.</summary>
    public string VersionIdentity { get; }

    public bool Equals(HistoricalDataset? other) =>
        other is not null &&
        string.Equals(Id, other.Id, StringComparison.Ordinal) &&
        string.Equals(VersionIdentity, other.VersionIdentity, StringComparison.Ordinal) &&
        CreatedAtUtc == other.CreatedAtUtc;

    public override bool Equals(object? obj) => Equals(obj as HistoricalDataset);

    public override int GetHashCode() => HashCode.Combine(Id, VersionIdentity, CreatedAtUtc);

    internal static string ComputeVersionIdentity(
        string source,
        string symbol,
        string interval,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int candleCount,
        string contentFingerprint,
        string sourceVersion)
    {
        var canonical = string.Join(
            "\n",
            source,
            symbol,
            interval,
            fromUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            toUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            candleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            contentFingerprint,
            sourceVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string CanonicalizeInterval(string interval)
    {
        var normalized = Required(interval, nameof(interval), 8).ToUpperInvariant();
        if (!SupportedIntervals.Contains(normalized))
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval is not supported.");
        }

        return normalized;
    }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{parameterName} cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }

    private static bool IsAligned(DateTimeOffset timestamp, string interval)
    {
        var utc = timestamp.UtcDateTime;
        return interval switch
        {
            "1M" => utc.Ticks % TimeSpan.TicksPerMinute == 0,
            "5M" => utc.Ticks % TimeSpan.TicksPerMinute == 0 && utc.Minute % 5 == 0,
            "10M" => utc.Ticks % TimeSpan.TicksPerMinute == 0 && utc.Minute % 10 == 0,
            "15M" => utc.Ticks % TimeSpan.TicksPerMinute == 0 && utc.Minute % 15 == 0,
            "30M" => utc.Ticks % TimeSpan.TicksPerMinute == 0 && utc.Minute % 30 == 0,
            "1H" => utc.Ticks % TimeSpan.TicksPerHour == 0,
            "4H" => utc.Ticks % TimeSpan.TicksPerHour == 0 && utc.Hour % 4 == 0,
            "1D" => utc.Ticks % TimeSpan.TicksPerDay == 0,
            _ => false
        };
    }
}
