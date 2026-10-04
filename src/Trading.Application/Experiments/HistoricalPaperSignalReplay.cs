using System.Collections.ObjectModel;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public sealed record HistoricalPaperReplaySeries(
    HistoricalDataset Dataset,
    IReadOnlyList<Candle> Candles);

public sealed record HistoricalPaperSignalReplayRequest(
    string FamilyId,
    int StrategyVersion,
    string Symbol,
    CandleInterval SignalInterval,
    string ParametersJson,
    DateTimeOffset FromSignalCloseUtc,
    DateTimeOffset ToSignalCloseUtc,
    IReadOnlyList<HistoricalPaperReplaySeries> Series);

public sealed record HistoricalPaperSignalObservation(
    DateTimeOffset SignalCloseUtc,
    ExperimentAnalysisResult Analysis);

/// <summary>
/// Read-only signal observations. There are no simulated fills, exits, qualifications,
/// point-in-time membership claims, or trading permissions in this result.
/// </summary>
public sealed record HistoricalPaperSignalReplayResult(
    string FamilyId,
    int StrategyVersion,
    string Symbol,
    string NormalizedParametersJson,
    IReadOnlyDictionary<CandleInterval, string> DatasetVersions,
    IReadOnlyDictionary<CandleInterval, HistoricalDataset> ArchiveManifests,
    IReadOnlyList<HistoricalPaperSignalObservation> Observations)
{
    public string EvidenceScope { get; } = "closed-candle-signal-only";
}

/// <summary>
/// Replays the scanner's exact approved direct-family evaluator at each closed signal
/// boundary, using immutable archived evidence cut off independently for every role.
/// Rotation and ensemble families require historical eligible-universe snapshots instead.
/// </summary>
public sealed class HistoricalPaperSignalReplay(ApprovedExperimentStrategyRegistry strategies)
{
    private const int MaximumSignalBoundaries = 2_000;
    private const int MaximumCandlesPerSeries = 100_000;
    private readonly ApprovedExperimentStrategyRegistry _strategies =
        strategies ?? throw new ArgumentNullException(nameof(strategies));

    public HistoricalPaperSignalReplayResult Run(
        HistoricalPaperSignalReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FamilyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Symbol);
        ArgumentNullException.ThrowIfNull(request.Series);
        cancellationToken.ThrowIfCancellationRequested();
        var definition = _strategies.ResolveDefinition(request.FamilyId, request.StrategyVersion);
        if (request.FamilyId is "platform.cross-sectional-momentum-rotation"
            or "platform.relative-strength-pullback-rotation"
            or "platform.regime-switching-ensemble")
            throw new InvalidOperationException(
                "Rotation and ensemble replay requires immutable point-in-time membership and complete daily universe evidence.");

        var profile = ApprovedConsensusStrategyProfiles.For(request.FamilyId)
            .FirstOrDefault(candidate => candidate.Signal == request.SignalInterval)
            ?? throw new ArgumentException("The signal interval has no approved timeframe profile.", nameof(request));
        if (!ApprovedStrategyParameters.TryNormalize(
                request.FamilyId, request.ParametersJson, out var parameters, out var error))
            throw new ArgumentException($"Saved strategy settings are invalid: {error}", nameof(request));

        var interval = TimeSpan.FromMinutes((int)request.SignalInterval);
        var from = request.FromSignalCloseUtc;
        var to = request.ToSignalCloseUtc;
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero
            || from == default || to < from
            || from.UtcTicks % interval.Ticks != 0
            || to.UtcTicks % interval.Ticks != 0
            || (to - from).Ticks / interval.Ticks >= MaximumSignalBoundaries)
            throw new ArgumentOutOfRangeException(nameof(request),
                $"Replay requires one to {MaximumSignalBoundaries} aligned, completed UTC signal boundaries.");

        var required = ApprovedConsensusStrategyProfiles.RequiredIntervals(
            request.FamilyId, profile.Signal);
        if (request.Series.Count != required.Count
            || request.Series.Any(item => item is null || item.Dataset is null || item.Candles is null)
            || request.Series.Select(item => item.Dataset.Interval).Distinct(StringComparer.Ordinal).Count()
                != request.Series.Count)
            throw new ArgumentException("Supply exactly one immutable archive per approved timeframe.", nameof(request));

        var archives = new Dictionary<CandleInterval, ReplayArchive>();
        foreach (var item in request.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Candles.Count is < 1 or > MaximumCandlesPerSeries
                || !string.Equals(item.Dataset.Symbol, request.Symbol, StringComparison.Ordinal))
                throw new ArgumentException("Every bounded archive must match the requested symbol.", nameof(request));
            var candles = item.Candles.ToArray();
            HistoricalCandleEvidenceValidator.Validate(item.Dataset, candles);
            var role = candles[0].Interval;
            if (!required.Contains(role) || !archives.TryAdd(role,
                    new ReplayArchive(item.Dataset, candles)))
                throw new ArgumentException("A required timeframe archive is missing or duplicated.", nameof(request));
        }

        var signal = archives[profile.Signal];
        if (from < signal.Candles[0].CloseTimeUtc || to > signal.Candles[^1].CloseTimeUtc)
            throw new ArgumentOutOfRangeException(nameof(request),
                "The signal archive must contain the entire requested closed-boundary range.");

        var results = new List<HistoricalPaperSignalObservation>();
        for (var close = from; close <= to; close += interval)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshots = new Dictionary<CandleInterval, ExperimentCandleSeries>();
            string? blockedReason = null;
            foreach (var role in required)
            {
                var source = archives[role];
                var duration = TimeSpan.FromMinutes((int)role);
                var expectedClose = new DateTimeOffset(
                    close.UtcTicks - close.UtcTicks % duration.Ticks, TimeSpan.Zero);
                var index = Array.BinarySearch(source.CloseTimes, expectedClose);
                if (index < ApprovedConsensusStrategyProfiles.RequiredHistory - 1)
                {
                    blockedReason = $"Missing {role} evidence or {ApprovedConsensusStrategyProfiles.RequiredHistory} closed warm-up candles at {expectedClose:O}.";
                    break;
                }

                var window = new ArraySegment<Candle>(source.Candles,
                    index + 1 - ApprovedConsensusStrategyProfiles.RequiredHistory,
                    ApprovedConsensusStrategyProfiles.RequiredHistory);
                snapshots.Add(role, new ExperimentCandleSeries(
                    request.Symbol, role, expectedClose, window, source.Dataset.VersionIdentity));
            }

            var analysis = blockedReason is not null
                ? ExperimentAnalysisResult.Blocked(blockedReason)
                : ContinuousPaperOpportunityScanner.EvaluateDirectCandidate(
                    _strategies, definition, snapshots[profile.Regime],
                    snapshots[profile.Signal], snapshots[profile.Execution], parameters,
                    request.FamilyId == "platform.three-swing-channel-divergence"
                        ? snapshots[CandleInterval.OneHour]
                        : null);
            results.Add(new HistoricalPaperSignalObservation(close, analysis));
        }

        var identities = archives.ToDictionary(
            item => item.Key, item => item.Value.Dataset.VersionIdentity);
        var manifests = archives.ToDictionary(
            item => item.Key, item => item.Value.Dataset);
        return new HistoricalPaperSignalReplayResult(
            request.FamilyId, request.StrategyVersion, request.Symbol, parameters,
            new ReadOnlyDictionary<CandleInterval, string>(identities),
            new ReadOnlyDictionary<CandleInterval, HistoricalDataset>(manifests),
            Array.AsReadOnly(results.ToArray()));
    }

    private sealed class ReplayArchive(HistoricalDataset dataset, Candle[] candles)
    {
        public HistoricalDataset Dataset { get; } = dataset;
        public Candle[] Candles { get; } = candles;
        public DateTimeOffset[] CloseTimes { get; } = candles.Select(candle => candle.CloseTimeUtc).ToArray();
    }
}
