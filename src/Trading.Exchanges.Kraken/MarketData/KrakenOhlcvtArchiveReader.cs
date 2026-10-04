using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

/// <summary>
/// Reads one trusted, offline Kraken OHLCVT CSV for one pair and native interval.
/// Gaps divide the evidence into contiguous runs; no-trade bars are never invented.
/// This reader does not persist or qualify a dataset for trading.
/// </summary>
public static class KrakenOhlcvtArchiveReader
{
    public const int MaximumCandles = 100_000;

    public static async Task<KrakenOhlcvtArchive> ReadAsync(
        TextReader reader,
        string symbol,
        CandleInterval interval,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (importedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Archive import time must be UTC.", nameof(importedAtUtc));
        if (importedAtUtc <= DateTimeOffset.UnixEpoch)
            throw new ArgumentOutOfRangeException(nameof(importedAtUtc), "Archive import time must follow the Unix epoch.");
        if (importedAtUtc > DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(importedAtUtc), "Archive import time cannot be in the future.");

        var duration = TimeSpan.FromMinutes(KrakenIntervalMap.ToKrakenMinutes(interval));
        var segments = new List<IReadOnlyList<Candle>>();
        var gaps = new List<KrakenOhlcvtGap>();
        var current = new List<Candle>();
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        DateTimeOffset? previousOpenUtc = null;
        var lineNumber = 0;
        var count = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } rawLine)
        {
            lineNumber++;
            var line = lineNumber == 1 ? rawLine.TrimStart('\uFEFF') : rawLine;
            fingerprint.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
            if (lineNumber == 1 && IsHeader(line))
                continue;
            if (++count > MaximumCandles)
                throw new InvalidDataException($"Kraken archive exceeds the {MaximumCandles} candle import limit.");

            var candle = ParseRow(line, symbol.Trim(), interval, duration, importedAtUtc, lineNumber);
            if (previousOpenUtc is { } previous)
            {
                if (candle.OpenTimeUtc <= previous)
                    throw InvalidRow(lineNumber, "candle timestamps must be strictly increasing and unique");

                var expectedOpenUtc = previous + duration;
                if (candle.OpenTimeUtc > expectedOpenUtc)
                {
                    segments.Add(Array.AsReadOnly(current.ToArray()));
                    current.Clear();
                    gaps.Add(new KrakenOhlcvtGap(
                        expectedOpenUtc,
                        candle.OpenTimeUtc,
                        (candle.OpenTimeUtc - expectedOpenUtc).Ticks / duration.Ticks));
                }
            }

            current.Add(candle);
            previousOpenUtc = candle.OpenTimeUtc;
        }

        if (current.Count == 0)
            throw new InvalidDataException("Kraken archive must contain at least one completed candle.");
        segments.Add(Array.AsReadOnly(current.ToArray()));
        return new KrakenOhlcvtArchive(
            Array.AsReadOnly(segments.ToArray()),
            Array.AsReadOnly(gaps.ToArray()),
            count,
            Convert.ToHexString(fingerprint.GetHashAndReset()));
    }

    private static Candle ParseRow(
        string line,
        string symbol,
        CandleInterval interval,
        TimeSpan duration,
        DateTimeOffset importedAtUtc,
        int lineNumber)
    {
        var fields = line.Split(',');
        if (fields.Length != 7
            || !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            || !decimal.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var open)
            || !decimal.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var high)
            || !decimal.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var low)
            || !decimal.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var close)
            || !decimal.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var volume)
            || !long.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var trades))
            throw InvalidRow(lineNumber, "expected time, open, high, low, close, volume, trades");

        if (open <= 0m || high < open || high < close || low <= 0m
            || low > open || low > close || close <= 0m || volume <= 0m || trades <= 0)
            throw InvalidRow(lineNumber, "OHLC, volume, or trade count is not valid");

        DateTimeOffset openUtc;
        try
        {
            openUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw InvalidRow(lineNumber, "Unix timestamp is outside the supported range");
        }

        if (openUtc.Ticks % duration.Ticks != 0 || openUtc >= importedAtUtc - duration)
            throw InvalidRow(lineNumber, "candle is misaligned or was not closed before import");

        return new Candle(symbol, interval, openUtc, openUtc + duration,
            open, high, low, close, volume, isClosed: true, isDerived: false);
    }

    private static bool IsHeader(string line) =>
        line.Equals("time,open,high,low,close,volume,trades", StringComparison.OrdinalIgnoreCase)
        || line.Equals("timestamp,open,high,low,close,volume,trades", StringComparison.OrdinalIgnoreCase);

    private static InvalidDataException InvalidRow(int number, string reason) =>
        new($"Kraken OHLCVT row {number}: {reason}.");
}

public sealed record KrakenOhlcvtGap(
    DateTimeOffset FirstMissingOpenUtc,
    DateTimeOffset NextObservedOpenUtc,
    long MissingCandleCount);

public sealed class KrakenOhlcvtArchive
{
    internal KrakenOhlcvtArchive(
        IReadOnlyList<IReadOnlyList<Candle>> segments,
        IReadOnlyList<KrakenOhlcvtGap> gaps,
        int candleCount,
        string normalizedCsvFingerprint)
    {
        Segments = segments;
        Gaps = gaps;
        CandleCount = candleCount;
        NormalizedCsvFingerprint = normalizedCsvFingerprint;
    }

    public IReadOnlyList<IReadOnlyList<Candle>> Segments { get; }
    public IReadOnlyList<KrakenOhlcvtGap> Gaps { get; }
    public int CandleCount { get; }

    /// <summary>SHA-256 of decoded CSV lines with LF endings and an optional leading BOM removed; not proof of origin.</summary>
    public string NormalizedCsvFingerprint { get; }
}
