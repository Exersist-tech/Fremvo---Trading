using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public sealed record ContinuousPaperScanResult(
    Guid OwnerId,
    DateTimeOffset SignalBoundaryUtc,
    int EligiblePairs,
    int EvaluatedCandidates,
    int QualifiedCandidates,
    int QueuedCandidates,
    int AdmittedCandidates,
    bool Persisted);

public interface IContinuousPaperOpportunityScanner
{
    Task<ContinuousPaperScanResult> ScanAsync(Guid ownerId, CancellationToken cancellationToken = default);
}

public sealed class DisabledContinuousPaperOpportunityScanner : IContinuousPaperOpportunityScanner
{
    public Task<ContinuousPaperScanResult> ScanAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ContinuousPaperScanResult(ownerId, DateTimeOffset.MinValue, 0, 0, 0, 0, 0, false));
    }
}

/// <summary>
/// Scans public Kraken Spot EUR history without allocating waiting workers. The activation row is
/// the durable admission ledger: only admitted opportunities occupy its ten bounded slots.
/// </summary>
public sealed class ContinuousPaperOpportunityScanner : IContinuousPaperOpportunityScanner
{
    private const int MaximumRememberedObservations = 500;

    private readonly PaperTrainingUniverseDiscovery _universe;
    private readonly IHistoricalCandleSource _candles;
    private readonly ApprovedExperimentStrategyRegistry _strategies;
    private readonly IPaperTrainingActivationRepository _activations;
    private readonly IExperimentWorkerRepository _workers;
    private readonly TimeProvider _time;

    public ContinuousPaperOpportunityScanner(
        PaperTrainingUniverseDiscovery universe,
        IHistoricalCandleSource candles,
        ApprovedExperimentStrategyRegistry strategies,
        IPaperTrainingActivationRepository activations,
        IExperimentWorkerRepository workers,
        TimeProvider time)
    {
        _universe = universe ?? throw new ArgumentNullException(nameof(universe));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _activations = activations ?? throw new ArgumentNullException(nameof(activations));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<ContinuousPaperScanResult> ScanAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("Owner is required.", nameof(ownerId));

        var activation = await _activations.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        var boundary = AlignDown(_time.GetUtcNow(), TimeSpan.FromMinutes(5));
        if (activation is not { IsActive: true })
            return new(ownerId, boundary, 0, 0, 0, 0, 0, false);
        var scanRunId = $"scan-run-{boundary:O}";
        if (activation.QualificationResults.Any(result =>
                result.DatasetFingerprint.Equals(scanRunId, StringComparison.Ordinal)))
            return new(ownerId, boundary, 0, 0, 0, 0, 0, true);

        var existingWorkers = await _workers.ListAsync(ownerId, cancellationToken).ConfigureAwait(false);
        var retainedSlots = new List<PaperTrainingWorkerSlot>();
        foreach (var slot in activation.Slots)
        {
            var worker = FindWorker(slot, activation.ChangedAtUtc, existingWorkers);
            if (slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal))
            {
                if (worker is null
                    || worker.PositionQuantity > 0m
                    || worker.Status is ExperimentWorkerStatus.Created
                        or ExperimentWorkerStatus.Running
                        or ExperimentWorkerStatus.Paused)
                {
                    retainedSlots.Add(slot);
                }
                continue;
            }

            if (worker is null)
                continue;
            if (worker.PositionQuantity > 0m)
            {
                retainedSlots.Add(slot);
                continue;
            }
            if (worker.Status != ExperimentWorkerStatus.Failed
                && worker.Status != ExperimentWorkerStatus.Completed)
            {
                worker.Complete();
                await _workers.SaveAsync(worker, cancellationToken).ConfigureAwait(false);
            }
        }
        var knownObservationIds = activation.QualificationResults
            .Where(result => result.Accepted || !result.PaperOnlyExploration)
            .Select(result => result.DatasetFingerprint)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);

        var universe = await _universe.DiscoverAsync(boundary, cancellationToken).ConfigureAwait(false);
        var series = await LoadSeriesAsync(universe, boundary, cancellationToken).ConfigureAwait(false);
        var candidates = new List<RankedCandidate>();
        var rejectedObservations = new List<PaperTrainingQualificationResult>();
        var evaluated = 0;
        foreach (var pair in universe)
        {
            foreach (var definition in _strategies.Definitions.OrderBy(value => value.FamilyId, StringComparer.Ordinal))
            {
                foreach (var profile in ApprovedConsensusStrategyProfiles.For(definition.FamilyId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var interval = profile.Signal;
                    if (boundary.UtcTicks % TimeSpan.FromMinutes((int)interval).Ticks != 0
                        || !series.TryGetValue((pair.Symbol, profile.Regime), out var regimeSeries)
                        || !series.TryGetValue((pair.Symbol, profile.Signal), out var signalSeries)
                        || !series.TryGetValue((pair.Symbol, profile.Execution), out var executionSeries))
                        continue;
                    evaluated++;
                    var observationId = ObservationId(
                        ownerId,
                        definition.FamilyId,
                        pair.Symbol,
                        interval,
                        signalSeries.AsOfUtc);
                    if (knownObservationIds.Contains(observationId))
                        continue;

                    var analysis = EvaluateCandidate(
                        definition.FamilyId,
                        pair,
                        regimeSeries,
                        signalSeries,
                        executionSeries,
                        universe,
                        series);
                    if (analysis.Outcome != ExperimentAnalysisOutcome.Analyzed
                        || analysis.Consensus is not { MandatoryVeto: false } consensus)
                    {
                        var disposition = analysis.Consensus?.MandatoryVeto == true
                            ? PaperTrainingCandidateDisposition.Vetoed
                            : analysis.Outcome == ExperimentAnalysisOutcome.NoCondition
                                ? PaperTrainingCandidateDisposition.Hold
                                : PaperTrainingCandidateDisposition.Rejected;
                        rejectedObservations.Add(new PaperTrainingQualificationResult(
                            0,
                            pair.Symbol,
                            false,
                            0m,
                            0,
                            0m,
                            observationId,
                            analysis.Reason,
                            definition.FamilyId,
                            PaperOnlyExploration: false,
                            interval,
                            disposition,
                            analysis.Consensus?.BullishCount,
                            analysis.Consensus?.RequiredAgreement,
                            signalSeries.AsOfUtc));
                        continue;
                    }
                    if (analysis.Value is not > 0m
                        || consensus.BullishCount < consensus.RequiredAgreement)
                    {
                        rejectedObservations.Add(new PaperTrainingQualificationResult(
                            0,
                            pair.Symbol,
                            false,
                            0m,
                            0,
                            0m,
                            observationId,
                            $"Not admitted: a flat paper worker requires an actionable BUY. {analysis.Reason}",
                            definition.FamilyId,
                            PaperOnlyExploration: false,
                            interval,
                            PaperTrainingCandidateDisposition.Bearish,
                            consensus.BullishCount,
                            consensus.RequiredAgreement,
                            signalSeries.AsOfUtc));
                        continue;
                    }
                    candidates.Add(new RankedCandidate(
                        observationId,
                        definition.FamilyId,
                        pair.Symbol,
                        interval,
                        signalSeries.AsOfUtc,
                        consensus.BullishCount,
                        consensus.RequiredAgreement,
                        pair.MedianDailyQuoteVolume,
                        analysis.Reason));
                }
            }
        }

        var ranked = candidates
            .OrderByDescending(candidate => candidate.Agreement)
            .ThenByDescending(candidate => candidate.Liquidity)
            .ThenBy(candidate => candidate.SignalCloseUtc)
            .ThenBy(candidate => candidate.StrategyId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Interval)
            .ToArray();
        var activeKeys = retainedSlots
            .Select(slot => (slot.StrategyId, slot.Symbol, slot.Interval))
            .ToHashSet();
        var symbolCounts = retainedSlots
            .GroupBy(slot => slot.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var strategyCounts = retainedSlots
            .GroupBy(slot => slot.StrategyId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var admitted = 0;
        var remaining = ranked.ToList();
        while (retainedSlots.Count < ExperimentWorker.MaxWorkersPerUser && remaining.Count > 0)
        {
            var candidate = remaining
                .Where(item => !activeKeys.Contains((item.StrategyId, item.Symbol, item.Interval)))
                .Where(item => symbolCounts.GetValueOrDefault(item.Symbol) < 2)
                .OrderBy(item => strategyCounts.ContainsKey(item.StrategyId))
                .ThenBy(item => strategyCounts.GetValueOrDefault(item.StrategyId))
                .ThenBy(item => symbolCounts.ContainsKey(item.Symbol))
                .ThenBy(item => symbolCounts.GetValueOrDefault(item.Symbol))
                .ThenBy(item => Array.IndexOf(ranked, item))
                .FirstOrDefault();
            if (candidate is null)
                break;
            remaining.Remove(candidate);
            if (activeKeys.Contains((candidate.StrategyId, candidate.Symbol, candidate.Interval)))
                continue;
            if (symbolCounts.GetValueOrDefault(candidate.Symbol) >= 2)
                continue;

            var template = PaperTrainingActivationService.ApprovedSlots.Single(
                slot => slot.StrategyId.Equals(candidate.StrategyId, StringComparison.Ordinal));
            var slotNumber = Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser)
                .First(number => retainedSlots.All(slot => slot.Slot != number));
            retainedSlots.Add(template with
            {
                Slot = slotNumber,
                Symbol = candidate.Symbol,
                Interval = candidate.Interval,
                Seed = StableSeed(candidate.ObservationId),
                ProvenanceId = $"scan-{candidate.ObservationId[..24]}"
            });
            activeKeys.Add((candidate.StrategyId, candidate.Symbol, candidate.Interval));
            symbolCounts[candidate.Symbol] = symbolCounts.GetValueOrDefault(candidate.Symbol) + 1;
            strategyCounts[candidate.StrategyId] = strategyCounts.GetValueOrDefault(candidate.StrategyId) + 1;
            admitted++;
        }

        var admittedIds = retainedSlots
            .Where(slot => slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal))
            .Select(slot => slot.ProvenanceId[5..])
            .ToHashSet(StringComparer.Ordinal);
        var universeObservations = universe.Select(pair => new PaperTrainingQualificationResult(
            0,
            pair.Symbol,
            false,
            0m,
            0,
            0m,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scanRunId}|{pair.Symbol}"))),
            $"{scanRunId}|Point-in-time eligible-universe member.",
            "platform.scanner-universe",
            PaperOnlyExploration: false,
            CandleInterval.OneDay));
        var currentObservations = rejectedObservations
            .Concat(ranked.Select((candidate, rank) => new PaperTrainingQualificationResult(
                rank + 1,
                candidate.Symbol,
                admittedIds.Contains(candidate.ObservationId[..24]),
                candidate.Agreement * 20m,
                0,
                0m,
                candidate.ObservationId,
                admittedIds.Contains(candidate.ObservationId[..24])
                    ? $"Admitted from rank {rank + 1}. {candidate.Reason}"
                    : $"Queued at rank {rank + 1}; capacity or concentration gate deferred admission. {candidate.Reason}",
                candidate.StrategyId,
                PaperOnlyExploration: true,
                candidate.Interval,
                admittedIds.Contains(candidate.ObservationId[..24])
                    ? PaperTrainingCandidateDisposition.Admitted
                    : PaperTrainingCandidateDisposition.Queued,
                candidate.Agreement,
                candidate.RequiredAgreement,
                candidate.SignalCloseUtc)))
            .Append(new PaperTrainingQualificationResult(
                0,
                "KRAKEN/EUR",
                true,
                0m,
                0,
                0m,
                scanRunId,
                $"Completed five-minute scan at {boundary:O}.",
                "platform.scanner",
                PaperOnlyExploration: true,
                CandleInterval.FiveMinutes))
            .Concat(universeObservations)
            .ToArray();
        var observations = activation.QualificationResults
            .Concat(currentObservations)
            .DistinctBy(result => result.DatasetFingerprint)
            .TakeLast(MaximumRememberedObservations)
            .ToArray();
        var updated = activation with
        {
            Slots = retainedSlots.OrderBy(slot => slot.Slot).ToArray(),
            Qualifications = observations
        };
        var persisted = await _activations.TrySaveAsync(
            updated,
            PaperTrainingActivationState.Active,
            cancellationToken).ConfigureAwait(false);
        return new(
            ownerId,
            boundary,
            universe.Count,
            evaluated,
            ranked.Length,
            Math.Max(0, ranked.Length - admitted),
            admitted,
            persisted);
    }

    private async Task<IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries>> LoadSeriesAsync(
        IReadOnlyList<PaperTrainingUniverseCandidate> universe,
        DateTimeOffset boundary,
        CancellationToken cancellationToken)
    {
        var loaded = new ConcurrentDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries>();
        var activeProfiles = _strategies.Definitions
            .SelectMany(definition => ApprovedConsensusStrategyProfiles.For(definition.FamilyId))
            .Where(profile => boundary.UtcTicks % TimeSpan.FromMinutes((int)profile.Signal).Ticks == 0)
            .ToArray();
        var requests = universe.SelectMany(pair => activeProfiles
            .SelectMany(profile => new[] { profile.Regime, profile.Signal, profile.Execution })
            .Distinct()
            .Select(interval => (pair.Symbol, interval)));
        await Parallel.ForEachAsync(
            requests,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            async (request, token) =>
            {
                try
                {
                    var duration = TimeSpan.FromMinutes((int)request.interval);
                    var expectedClose = AlignDown(boundary, duration);
                    var fetched = await _candles.FetchAsync(
                        request.Symbol,
                        request.interval,
                        expectedClose.AddTicks(-duration.Ticks * (ApprovedConsensusStrategyProfiles.RequiredHistory + 2)),
                        token).ConfigureAwait(false);
                    var closed = fetched
                        .Where(candle => candle.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase)
                            && candle.Interval == request.interval
                            && candle.CanBeUsedForClosedCandleSignal
                            && candle.CloseTimeUtc <= expectedClose)
                        .OrderBy(candle => candle.OpenTimeUtc)
                        .TakeLast(ApprovedConsensusStrategyProfiles.RequiredHistory)
                        .ToArray();
                    if (closed.Length != ApprovedConsensusStrategyProfiles.RequiredHistory
                        || closed[^1].CloseTimeUtc != expectedClose
                        || closed.Zip(closed.Skip(1), (left, right) => left.CloseTimeUtc == right.OpenTimeUtc)
                            .Any(contiguous => !contiguous))
                    {
                        return;
                    }
                    loaded.TryAdd(
                        (request.Symbol, request.interval),
                        new ExperimentCandleSeries(request.Symbol, request.interval, expectedClose, closed));
                }
                catch (MarketDataSourceException)
                {
                    // A single unavailable public series rejects only that candidate.
                }
            }).ConfigureAwait(false);
        return loaded;
    }

    private ExperimentAnalysisResult EvaluateCandidate(
            string familyId,
            PaperTrainingUniverseCandidate pair,
            ExperimentCandleSeries regime,
            ExperimentCandleSeries signal,
            ExperimentCandleSeries execution,
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries)
        {
            if (familyId == "platform.cross-sectional-momentum-rotation")
                return EvaluateCrossSectionalMomentum(pair, universe, allSeries, signal.AsOfUtc);
            if (familyId == "platform.relative-strength-pullback-rotation")
                return EvaluateRelativeStrengthPullback(pair, universe, allSeries, signal.AsOfUtc);
            if (familyId == "platform.regime-switching-ensemble")
                return EvaluateRegimeConsensus(pair, universe, allSeries, regime);
            return _strategies.EvaluateProfile(familyId, regime, signal, execution, "{}");
        }

    private ExperimentAnalysisResult EvaluateRegimeConsensus(
        PaperTrainingUniverseCandidate pair,
        IReadOnlyList<PaperTrainingUniverseCandidate> universe,
        IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
        ExperimentCandleSeries regime)
    {
        const string family = "platform.regime-switching-ensemble";
        var ranked = RankUniverse(universe, allSeries, regime.AsOfUtc);
        if (ranked.Length != universe.Count)
            return Vetoed(family, "complete point-in-time breadth evidence is unavailable");

        var breadth = ranked.Count(item => item.AboveEma200) / (decimal)ranked.Length;
        var candles = regime.Candles;
        var fast = new ExponentialMovingAverageCalculator(50).Calculate(candles).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(200).Calculate(candles).Value!.Value;
        var priorFast = new ExponentialMovingAverageCalculator(50)
            .Calculate(candles.Take(candles.Count - 5).ToArray()).Value!.Value;
        var atr = new AverageTrueRangeCalculator(14).Calculate(candles).Value!.Value;
        var atrPercent = atr / candles[^1].Close * 100m;
        var bandwidth = Bandwidth(candles);
        var bandwidthHistory = Enumerable.Range(100, candles.Count - 99)
            .Select(count => Bandwidth(candles.Take(count).ToArray()))
            .ToArray();
        var bandwidthPercentile = bandwidthHistory.Count(value => value <= bandwidth)
            / (decimal)bandwidthHistory.Length;

        if (atrPercent >= 8m || breadth < .20m)
            return Vetoed(family, "CRISIS breadth or volatility permits no new Spot exposure");
        if (fast < slow && fast < priorFast && candles[^1].Close < slow)
        {
            return ExperimentAnalysisResult.FromConsensus(
                family,
                Enumerable.Range(1, 5)
                    .Select(index => new ExperimentSignalCheck(
                        $"trend-down-spot-exit-{index}",
                        ExperimentSignalDirection.Bearish,
                        "TREND_DOWN permits Spot reduction only."))
                    .ToArray(),
                5);
        }

        string[] mappedFamilies;
        if (bandwidthPercentile <= .20m)
            mappedFamilies = ["platform.volatility-compression-breakout"];
        else if (fast > slow && fast > priorFast && candles[^1].Close > slow)
            mappedFamilies =
            [
                "platform.ema-trend-continuation",
                "platform.donchian-breakout-ensemble",
                "platform.rsi-pullback"
            ];
        else if (atrPercent >= 4m)
            return Vetoed(family, "EXPANSION manages existing positions but blocks late new entries");
        else
            mappedFamilies = ["platform.bollinger-mean-reversion"];

        var component = mappedFamilies
            .Select(mappedFamily => EvaluateMapped(mappedFamily, pair.Symbol, allSeries))
            .FirstOrDefault(result => result is { Outcome: ExperimentAnalysisOutcome.Analyzed, Value: > 0m });
        if (component?.Consensus is null)
            return Vetoed(family, "no independently approved strategy mapped to the detected regime reached entry consensus");

        return ExperimentAnalysisResult.FromConsensus(
            family,
            component.Consensus.Checks
                .Select(check => check with
                {
                    Rationale = $"{check.Rationale} Complete eligible-universe breadth is {breadth:P2}."
                })
                .ToArray(),
            component.Consensus.RequiredAgreement);
    }

    private static decimal Bandwidth(IReadOnlyList<Candle> candles)
    {
        var bands = new BollingerBandsCalculator(20, 2m).Calculate(candles).Value!.Value;
        return bands.Middle <= 0m
            ? decimal.MaxValue
            : (bands.Upper - bands.Lower) / bands.Middle * 100m;
    }

    private ExperimentAnalysisResult? EvaluateMapped(
        string familyId,
        string symbol,
        IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries)
    {
        foreach (var profile in ApprovedConsensusStrategyProfiles.For(familyId))
        {
            if (allSeries.TryGetValue((symbol, profile.Regime), out var regime)
                && allSeries.TryGetValue((symbol, profile.Signal), out var signal)
                && allSeries.TryGetValue((symbol, profile.Execution), out var execution))
            {
                return _strategies.EvaluateProfile(familyId, regime, signal, execution, "{}");
            }
        }
        return null;
    }

    private static ExperimentAnalysisResult EvaluateCrossSectionalMomentum(
            PaperTrainingUniverseCandidate pair,
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
            DateTimeOffset asOfUtc)
        {
            const string family = "platform.cross-sectional-momentum-rotation";
            var observations = RankUniverse(universe, allSeries, asOfUtc)
                .OrderByDescending(observation => observation.MomentumScore)
                .ThenBy(observation => observation.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (observations.Length != universe.Count)
                return Vetoed(family, "the complete point-in-time daily universe is unavailable");
            var selected = observations.Single(observation =>
                observation.Symbol.Equals(pair.Symbol, StringComparison.OrdinalIgnoreCase));
            var topCount = Math.Max(1, (int)Math.Ceiling(observations.Length * .20m));
            var top = Array.IndexOf(observations, selected) < topCount;
            var correlationSafe = selected.Symbol.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase)
                || decimal.Abs(selected.CorrelationToBenchmark) <= .90m;
            return ExperimentAnalysisResult.FromConsensus(
                family,
                [
                    SignalCheck("top-momentum-percentile", top, "Instrument must rank in the top 20% of the complete eligible universe."),
                    SignalCheck("positive-absolute-return", selected.LongReturn > 0m, "Ninety-day absolute return must be positive."),
                    SignalCheck("daily-long-term-trend", selected.AboveEma200, "Daily close must be above EMA 200."),
                    SignalCheck("liquidity-and-history", selected.MedianQuoteVolume >= 1_000_000m, "Point-in-time history and platform daily-liquidity requirements must pass."),
                    SignalCheck("volatility-and-correlation", selected.Volatility <= .10m && correlationSafe, "Volatility and XBT-correlation concentration limits must pass.")
                ],
                5);
        }

    private static ExperimentAnalysisResult EvaluateRelativeStrengthPullback(
            PaperTrainingUniverseCandidate pair,
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
            DateTimeOffset asOfUtc)
        {
            const string family = "platform.relative-strength-pullback-rotation";
            var observations = RankUniverse(universe, allSeries, asOfUtc)
                .OrderByDescending(observation => observation.RelativeStrengthScore)
                .ThenBy(observation => observation.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (observations.Length != universe.Count
                || !allSeries.TryGetValue((pair.Symbol, CandleInterval.FourHours), out var setup)
                || !allSeries.TryGetValue((pair.Symbol, CandleInterval.OneHour), out var execution)
                || setup.Candles.Count < 51
                || execution.Candles.Count < 2)
                return Vetoed(family, "complete daily ranking, 4h setup, and 1h execution evidence is required");

            var selected = observations.Single(observation =>
                observation.Symbol.Equals(pair.Symbol, StringComparison.OrdinalIgnoreCase));
            var topCount = Math.Max(1, (int)Math.Ceiling(observations.Length * .20m));
            var top = Array.IndexOf(observations, selected) < topCount;
            var setupClose = setup.Candles[^1].Close;
            var ema20 = new ExponentialMovingAverageCalculator(20).Calculate(setup.Candles).Value!.Value;
            var ema50 = new ExponentialMovingAverageCalculator(50).Calculate(setup.Candles).Value!.Value;
            var distance = Math.Min(decimal.Abs(setupClose - ema20), decimal.Abs(setupClose - ema50));
            var atr = new AverageTrueRangeCalculator(14).Calculate(setup.Candles).Value!.Value;
            var rsi = new RelativeStrengthIndexCalculator(14).Calculate(setup.Candles).Value!.Value;
            var priorRsi = new RelativeStrengthIndexCalculator(14)
                .Calculate(setup.Candles.Take(setup.Candles.Count - 1).ToArray()).Value!.Value;
            return ExperimentAnalysisResult.FromConsensus(
                family,
                [
                    SignalCheck("top-relative-strength", top, "Asset must remain in the top 20% relative-strength group."),
                    SignalCheck("positive-daily-trend", selected.LongReturn > 0m && selected.AboveEma200, "Daily absolute trend must remain positive."),
                    SignalCheck("bounded-four-hour-pullback", distance <= atr && setupClose >= ema50, "Four-hour price must pull within 1 ATR of EMA 20 or 50 without losing EMA 50 structure."),
                    SignalCheck("four-hour-rsi-cooled", rsi is >= 35m and <= 55m && rsi >= priorRsi, "Four-hour RSI must cool and then stop deteriorating."),
                    SignalCheck("one-hour-confirmation", execution.Candles[^1].Close > execution.Candles[^2].High, "A closed one-hour candle must confirm renewed upside.")
                ],
                5);
        }

    private static CrossSectionalRankEvidence[] RankUniverse(
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
            DateTimeOffset asOfUtc)
        {
            var daily = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in universe)
            {
                if (!allSeries.TryGetValue((pair.Symbol, CandleInterval.OneDay), out var series)
                    || series.AsOfUtc > asOfUtc
                    || series.Candles.Count < 201)
                {
                    return [];
                }
                daily.Add(pair.Symbol, series.Candles);
            }
            return CrossSectionalConsensusRanking.Evaluate(daily);
        }

    private static ExperimentSignalCheck SignalCheck(string id, bool bullish, string rationale) =>
            new(id, bullish ? ExperimentSignalDirection.Bullish : ExperimentSignalDirection.Neutral, rationale);

    private static ExperimentAnalysisResult Vetoed(string family, string reason) =>
            ExperimentAnalysisResult.FromConsensus(
                family,
                Enumerable.Range(1, 5)
                    .Select(index => SignalCheck($"unavailable-{index}", false, reason))
                    .ToArray(),
                5,
                true,
                reason);

    private static ExperimentWorker? FindWorker(
        PaperTrainingWorkerSlot slot,
        DateTimeOffset activationChangedAtUtc,
        IReadOnlyCollection<ExperimentWorker> workers)
    {
        var name = slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal)
            ? WorkerName(slot)
            : $"Paper training {slot.Slot} {activationChangedAtUtc:yyyyMMddHHmmssfffffff}";
        return workers.SingleOrDefault(candidate => candidate.Name.Equals(name, StringComparison.Ordinal));
    }

    public static string WorkerName(PaperTrainingWorkerSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal)
            ? $"Paper opportunity {slot.ProvenanceId}"
            : $"Paper training {slot.Slot}";
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % interval.Ticks, TimeSpan.Zero);
    }

    private static string ObservationId(
        Guid ownerId,
        string strategyId,
        string symbol,
        CandleInterval interval,
        DateTimeOffset signalCloseUtc) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ownerId:D}|{strategyId}|{symbol.ToUpperInvariant()}|{(int)interval}|{signalCloseUtc:O}")));

    private static int StableSeed(string observationId) =>
        BitConverter.ToInt32(Convert.FromHexString(observationId), 0) & int.MaxValue;

    private sealed record RankedCandidate(
        string ObservationId,
        string StrategyId,
        string Symbol,
        CandleInterval Interval,
        DateTimeOffset SignalCloseUtc,
        int Agreement,
        int RequiredAgreement,
        decimal Liquidity,
        string Reason);
}
