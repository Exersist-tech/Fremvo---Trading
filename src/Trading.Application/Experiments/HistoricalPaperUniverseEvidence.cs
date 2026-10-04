using System.Collections.ObjectModel;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

/// <summary>
/// Matches immutable archive windows to observed scanner inputs. This attests
/// inputs, not rotation decisions, fills, or performance.
/// </summary>
public static class HistoricalPaperUniverseEvidence
{
    public const int MaximumTotalCandles = 200_000;

    public static void Preflight(
        PaperScanUniverseSnapshot snapshot,
        IReadOnlyList<HistoricalDataset> manifests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(manifests);
        cancellationToken.ThrowIfCancellationRequested();
        snapshot.Validate();
        if (snapshot.Members.Count == 0 || manifests.Count != snapshot.Members.Count)
            throw new ArgumentException(
                "Supply one recorded, complete daily archive for every observed member.", nameof(manifests));
        if (snapshot.DailyEvidence.Any(item => item.CandleFingerprint is null))
            throw new InvalidOperationException(
                "The scanner did not record a complete daily window for every observed member.");

        var expectedClose = DailyClose(snapshot.SignalBoundaryUtc);
        var earliestOpen = expectedClose.AddDays(-ApprovedConsensusStrategyProfiles.RequiredHistory);
        var latestOpen = expectedClose.AddDays(-1);
        var memberSymbols = snapshot.Members.Select(item => item.Symbol)
            .ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalCandles = 0;
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manifest is null
                || !memberSymbols.Contains(manifest.Symbol)
                || !seen.Add(manifest.Symbol)
                || manifest.Interval != "1D"
                || manifest.CandleCount is < ApprovedConsensusStrategyProfiles.RequiredHistory or > 100_000
                || manifest.CandleCount > MaximumTotalCandles - totalCandles
                || manifest.CandleCount !=
                    (manifest.ToUtc - manifest.FromUtc).Ticks / TimeSpan.TicksPerDay + 1
                || manifest.FromUtc > earliestOpen
                || manifest.ToUtc < latestOpen)
                throw new ArgumentException(
                    "Daily archive manifests must cover every member's bounded scan window exactly once.",
                    nameof(manifests));
            totalCandles += manifest.CandleCount;
        }
    }

    public static IReadOnlyDictionary<string, ExperimentCandleSeries> Verify(
        PaperScanUniverseSnapshot snapshot,
        IReadOnlyList<HistoricalPaperReplaySeries> archives,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(archives);
        cancellationToken.ThrowIfCancellationRequested();
        if (archives.Any(item => item is null || item.Dataset is null || item.Candles is null))
            throw new ArgumentException(
                "Supply exactly one verified daily archive for every observed member.", nameof(archives));
        Preflight(snapshot, archives.Select(item => item.Dataset).ToArray(), cancellationToken);
        var expectedClose = DailyClose(snapshot.SignalBoundaryUtc);
        var windows = new Dictionary<string, ExperimentCandleSeries>(StringComparer.Ordinal);
        foreach (var item in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Candles.Count != item.Dataset.CandleCount)
                throw new ArgumentException("Archive scope or bounded daily history is invalid.", nameof(archives));
            var copy = item.Candles.ToArray();
            HistoricalCandleEvidenceValidator.Validate(item.Dataset, copy);
            var window = copy
                .TakeWhile(candle => candle.CloseTimeUtc <= expectedClose)
                .TakeLast(ApprovedConsensusStrategyProfiles.RequiredHistory)
                .ToArray();
            if (window.Length != ApprovedConsensusStrategyProfiles.RequiredHistory
                || window[^1].CloseTimeUtc != expectedClose)
                throw new InvalidOperationException("A member lacks 320 closed daily candles at the scanner boundary.");
            var series = new ExperimentCandleSeries(item.Dataset.Symbol, CandleInterval.OneDay,
                expectedClose, Array.AsReadOnly(window), item.Dataset.VersionIdentity);
            snapshot.VerifyDailyWindow(series);
            if (!windows.TryAdd(item.Dataset.Symbol, series))
                throw new ArgumentException("A member's daily archive was supplied more than once.", nameof(archives));
        }

        return new ReadOnlyDictionary<string, ExperimentCandleSeries>(windows);
    }

    public static void PreflightSeries(
        PaperScanUniverseSnapshot snapshot,
        HistoricalDataset manifest,
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        cancellationToken.ThrowIfCancellationRequested();
        snapshot.Validate();
        if (snapshot.SeriesEvidence is null)
            throw new InvalidOperationException(
                "This scan predates recorded timeframe evidence; it cannot attest an archived role.");
        if (!Enum.IsDefined(interval) || interval == CandleInterval.None)
            throw new ArgumentOutOfRangeException(nameof(interval));

        var duration = TimeSpan.FromMinutes((int)interval);
        var recorded = snapshot.SeriesEvidence.SingleOrDefault(item =>
            item.Symbol == symbol && item.Interval == interval);
        if (recorded is null)
            throw new InvalidOperationException(
                "The scanner did not record this member's complete timeframe window.");
        if (manifest.Symbol != symbol
            || HistoricalDataset.GetIntervalDuration(manifest.Interval) != duration
            || manifest.CandleCount is < ApprovedConsensusStrategyProfiles.RequiredHistory or > 100_000
            || manifest.CandleCount != (manifest.ToUtc - manifest.FromUtc).Ticks / duration.Ticks + 1
            || manifest.FromUtc > recorded.AsOfUtc.AddTicks(-duration.Ticks
                * ApprovedConsensusStrategyProfiles.RequiredHistory)
            || manifest.ToUtc < recorded.AsOfUtc.AddTicks(-duration.Ticks))
            throw new ArgumentException(
                "The selected bounded archive does not cover the recorded scanner timeframe.",
                nameof(manifest));
    }

    public static ExperimentCandleSeries VerifySeries(
        PaperScanUniverseSnapshot snapshot,
        HistoricalPaperReplaySeries archive,
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(archive.Candles);
        PreflightSeries(snapshot, archive.Dataset, symbol, interval, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (archive.Candles.Count != archive.Dataset.CandleCount)
            throw new ArgumentException("Archive payload count does not match its manifest.", nameof(archive));
        var candles = archive.Candles.ToArray();
        HistoricalCandleEvidenceValidator.Validate(archive.Dataset, candles);
        var duration = TimeSpan.FromMinutes((int)interval);
        var expectedClose = new DateTimeOffset(
            snapshot.SignalBoundaryUtc.UtcTicks
                - snapshot.SignalBoundaryUtc.UtcTicks % duration.Ticks, TimeSpan.Zero);
        var window = candles.TakeWhile(item => item.CloseTimeUtc <= expectedClose)
            .TakeLast(ApprovedConsensusStrategyProfiles.RequiredHistory)
            .ToArray();
        if (window.Length != ApprovedConsensusStrategyProfiles.RequiredHistory
            || window[^1].CloseTimeUtc != expectedClose)
            throw new InvalidOperationException(
                "The archive lacks 320 closed candles at the recorded timeframe boundary.");
        var series = new ExperimentCandleSeries(symbol, interval, expectedClose,
            Array.AsReadOnly(window), archive.Dataset.VersionIdentity);
        snapshot.VerifySeriesWindow(series);
        if (interval == CandleInterval.OneDay)
            snapshot.VerifyDailyWindow(series);
        return series;
    }

    public static void PreflightContext(
        PaperScanUniverseSnapshot snapshot,
        IReadOnlyList<HistoricalDataset> manifests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(manifests);
        cancellationToken.ThrowIfCancellationRequested();
        snapshot.Validate();
        if (snapshot.SeriesEvidence is not { Count: > 0 } roles)
            throw new InvalidOperationException(
                "The scan has no recorded timeframe windows to verify.");
        if (manifests.Count != roles.Count)
            throw new ArgumentException(
                "Supply exactly one bounded archive for every recorded scanner timeframe.",
                nameof(manifests));

        var seen = new HashSet<(string Symbol, CandleInterval Interval)>();
        var total = 0;
        foreach (var manifest in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (manifest is null || manifest.CandleCount > MaximumTotalCandles - total)
                throw new ArgumentException("The complete archive set exceeds its bounded input limit.",
                    nameof(manifests));
            var role = roles.SingleOrDefault(item =>
                item.Symbol == manifest.Symbol
                && TimeSpan.FromMinutes((int)item.Interval)
                    == HistoricalDataset.GetIntervalDuration(manifest.Interval));
            if (role is null || !seen.Add((role.Symbol, role.Interval)))
                throw new ArgumentException(
                    "Archive roles must match every recorded scanner window exactly once.",
                    nameof(manifests));
            PreflightSeries(snapshot, manifest, role.Symbol, role.Interval, cancellationToken);
            total += manifest.CandleCount;
        }
    }

    public static IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries>
        VerifyContext(
            PaperScanUniverseSnapshot snapshot,
            IReadOnlyList<HistoricalPaperReplaySeries> archives,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archives);
        cancellationToken.ThrowIfCancellationRequested();
        if (archives.Any(item => item is null || item.Dataset is null || item.Candles is null))
            throw new ArgumentException("Every scanner role needs a verified archive.", nameof(archives));
        PreflightContext(snapshot, archives.Select(item => item.Dataset).ToArray(), cancellationToken);
        var windows = new Dictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries>();
        foreach (var archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var role = snapshot.SeriesEvidence!.Single(item =>
                item.Symbol == archive.Dataset.Symbol
                && TimeSpan.FromMinutes((int)item.Interval)
                    == HistoricalDataset.GetIntervalDuration(archive.Dataset.Interval));
            var series = VerifySeries(snapshot, archive, role.Symbol, role.Interval, cancellationToken);
            windows.Add((role.Symbol, role.Interval), series);
        }
        return new ReadOnlyDictionary<(string Symbol, CandleInterval Interval),
            ExperimentCandleSeries>(windows);
    }

    private static DateTimeOffset DailyClose(DateTimeOffset boundary) =>
        new(boundary.UtcTicks - boundary.UtcTicks % TimeSpan.FromDays(1).Ticks,
            TimeSpan.Zero);
}
