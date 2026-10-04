using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Risk;

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

public interface IPaperScanEvidenceStager
{
    Task<bool> StageAsync(
        string symbol,
        DateTimeOffset boundaryUtc,
        IReadOnlyCollection<ExperimentCandleSeries> series,
        CancellationToken cancellationToken = default);

    Task<bool> StageUniverseDailyAsync(
        string symbol,
        DateTimeOffset boundaryUtc,
        IReadOnlyCollection<ExperimentCandleSeries> series,
        CancellationToken cancellationToken = default) =>
        StageAsync(symbol, boundaryUtc, series, cancellationToken);
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
    private static readonly string[] s_regimeComponents =
    [
        "platform.volatility-compression-breakout", "platform.ema-trend-continuation",
        "platform.donchian-breakout-ensemble", "platform.rsi-pullback",
        "platform.bollinger-mean-reversion"
    ];

    private readonly PaperTrainingUniverseDiscovery _universe;
    private readonly IHistoricalCandleSource _candles;
    private readonly ApprovedExperimentStrategyRegistry _strategies;
    private readonly IPaperTrainingActivationRepository _activations;
    private readonly IExperimentWorkerRepository _workers;
    private readonly IPaperScanEvidenceStager _stager;
    private readonly TimeProvider _time;
    private readonly IExperimentPaperExecutionLedger _executions;
    private readonly IPaperWorkerAdmissionLimit _admissionLimit;

    public ContinuousPaperOpportunityScanner(
        PaperTrainingUniverseDiscovery universe,
        IHistoricalCandleSource candles,
        ApprovedExperimentStrategyRegistry strategies,
        IPaperTrainingActivationRepository activations,
        IExperimentWorkerRepository workers,
        IPaperScanEvidenceStager stager,
        TimeProvider time,
        IExperimentPaperExecutionLedger executions)
        : this(universe, candles, strategies, activations, workers, stager, time, executions,
            new LegacyPaperWorkerAdmissionLimit())
    {
    }

    public ContinuousPaperOpportunityScanner(
        PaperTrainingUniverseDiscovery universe,
        IHistoricalCandleSource candles,
        ApprovedExperimentStrategyRegistry strategies,
        IPaperTrainingActivationRepository activations,
        IExperimentWorkerRepository workers,
        IPaperScanEvidenceStager stager,
        TimeProvider time,
        IExperimentPaperExecutionLedger executions,
        IPaperWorkerAdmissionLimit admissionLimit)
    {
        _universe = universe ?? throw new ArgumentNullException(nameof(universe));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        _activations = activations ?? throw new ArgumentNullException(nameof(activations));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _admissionLimit = admissionLimit ?? throw new ArgumentNullException(nameof(admissionLimit));
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
            return new(ownerId, boundary, 0, 0, 0, 0, 0, false);

        var existingWorkers = await _workers.ListAsync(ownerId, cancellationToken).ConfigureAwait(false);
        var strategyAssignments = activation.ConfiguredStrategies;
        var assignedStrategyIds = strategyAssignments
            .Select(assignment => assignment.StrategyId)
            .ToHashSet(StringComparer.Ordinal);
        var strategyParameters = activation.ConfiguredStrategyParameters
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var assignment in strategyAssignments)
            strategyParameters[assignment.StrategyId] = assignment.StrategyParameters;
        var definitions = _strategies.Definitions
            .Where(definition => assignedStrategyIds.Count == 0
                || assignedStrategyIds.Contains(definition.FamilyId))
            .OrderBy(value => value.FamilyId, StringComparer.Ordinal)
            .ToArray();
        if (strategyAssignments.Count > 0
            && definitions.Length != assignedStrategyIds.Count)
        {
            throw new InvalidOperationException(
                "An active worker slot references a strategy that is not registered in the approved runtime.");
        }
        var retainedSlots = new List<PaperTrainingWorkerSlot>();
        foreach (var slot in activation.Slots)
        {
            var worker = FindWorker(slot, activation.ChangedAtUtc, existingWorkers);
            var unresolved = worker is not null && await _executions
                .HasUnresolvedAsync(ownerId, worker.Id, cancellationToken).ConfigureAwait(false);
            if (slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal))
            {
                if (worker is null
                    || worker.PositionQuantity > 0m
                    || unresolved
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
            if (worker.PositionQuantity > 0m || unresolved)
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
        var series = await LoadSeriesAsync(
            universe,
            boundary,
            definitions,
            cancellationToken).ConfigureAwait(false);
        var evaluatedFamilies = definitions.Select(item => item.FamilyId)
            .ToHashSet(StringComparer.Ordinal);
        var strategyEvidence = _strategies.Definitions
            .OrderBy(item => item.FamilyId, StringComparer.Ordinal)
            .Select(item =>
            {
                var valid = ApprovedStrategyParameters.TryNormalize(item.FamilyId,
                    strategyParameters.GetValueOrDefault(item.FamilyId, "{}"),
                    out var normalized, out _);
                return new PaperScanStrategyEvidence(item.FamilyId, item.Version,
                    valid ? normalized : string.Empty,
                    evaluatedFamilies.Contains(item.FamilyId), valid);
            })
            .ToArray();
        var universeSnapshot = PaperScanUniverseSnapshot.Capture(
            ownerId, boundary, _time.GetUtcNow(), universe, _universe.Policy, series, strategyEvidence);
        var candidates = new List<RankedCandidate>();
        var rejectedObservations = new List<PaperTrainingQualificationResult>();
        var evaluated = 0;
        foreach (var pair in universe)
        {
            foreach (var definition in definitions)
            {
                foreach (var profile in ApprovedConsensusStrategyProfiles.For(definition.FamilyId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var interval = profile.Signal;
                    if (!IsEvaluationBoundary(definition, profile, boundary)
                        || !series.TryGetValue((pair.Symbol, profile.Regime), out var regimeSeries)
                        || !series.TryGetValue((pair.Symbol, profile.Signal), out var signalSeries)
                        || !series.TryGetValue((pair.Symbol, profile.Execution), out var executionSeries))
                        continue;
                    evaluated++;
                    var observationCloseUtc = definition.FamilyId == "platform.relative-strength-pullback-rotation"
                        && definition.Version >= 5
                        ? executionSeries.AsOfUtc
                        : signalSeries.AsOfUtc;
                    var observationId = ObservationId(
                        ownerId,
                        definition.FamilyId,
                        pair.Symbol,
                        interval,
                        observationCloseUtc);
                    if (knownObservationIds.Contains(observationId))
                        continue;

                    var analysis = EvaluateCandidate(
                        _strategies,
                        definition,
                        pair,
                        regimeSeries,
                        signalSeries,
                        executionSeries,
                        universe,
                        series,
                        strategyParameters.GetValueOrDefault(definition.FamilyId, "{}"),
                        strategyParameters);
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
                            observationCloseUtc));
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
                            observationCloseUtc));
                        continue;
                    }
                    candidates.Add(new RankedCandidate(
                        observationId,
                        definition.FamilyId,
                        pair.Symbol,
                        interval,
                        observationCloseUtc,
                        consensus.BullishCount,
                        consensus.RequiredAgreement,
                        pair.MedianDailyQuoteVolume,
                        analysis.Reason,
                        pair.PairFilters,
                        analysis.SelectedComponent,
                        definition.FamilyId is "platform.cross-sectional-momentum-rotation"
                            or "platform.relative-strength-pullback-rotation"
                            or "platform.regime-switching-ensemble"
                            ? universe.Select(item => item.Symbol)
                                .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase).ToArray()
                            : null));
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
        var availableAssignments = strategyAssignments
            .Where(assignment => retainedSlots.All(slot => slot.Slot != assignment.Slot))
            .OrderBy(assignment => assignment.Slot)
            .ToList();
        var symbolCounts = retainedSlots
            .GroupBy(slot => slot.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var strategyCounts = retainedSlots
            .GroupBy(slot => slot.StrategyId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var admitted = 0;
        var unstaged = new HashSet<string>(StringComparer.Ordinal);
        var stagedDaily = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = ranked.ToList();
        var previouslyRetained = retainedSlots.Count;
        var maximum = await _admissionLimit.GetMaximumAsync(ownerId, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (maximum is < 0 or > ExperimentWorker.MaxWorkersPerUser)
            throw new InvalidOperationException("The paper entitlement has an invalid worker ceiling.");
        while (retainedSlots.Count < maximum && remaining.Count > 0)
        {
            var eligibleCandidates = remaining
                .Where(item => !activeKeys.Contains((item.StrategyId, item.Symbol, item.Interval)))
                .Where(item => symbolCounts.GetValueOrDefault(item.Symbol) < 2);
            if (strategyAssignments.Count > 0)
            {
                eligibleCandidates = eligibleCandidates.Where(item =>
                    availableAssignments.Any(assignment =>
                        assignment.StrategyId.Equals(item.StrategyId, StringComparison.Ordinal)));
            }
            var candidate = eligibleCandidates
                .OrderBy(item => Array.IndexOf(ranked, item))
                .ThenBy(item => item.StrategyId, StringComparer.Ordinal)
                .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (candidate is null)
                break;
            remaining.Remove(candidate);
            if (activeKeys.Contains((candidate.StrategyId, candidate.Symbol, candidate.Interval)))
                continue;
            if (symbolCounts.GetValueOrDefault(candidate.Symbol) >= 2)
                continue;

            var required = ApprovedConsensusStrategyProfiles.RequiredIntervals(
                    candidate.StrategyId, candidate.Interval)
                .Concat(candidate.SelectedComponent is null
                    ? [] : ApprovedConsensusStrategyProfiles.RequiredIntervals(
                        candidate.SelectedComponent.FamilyId,
                        candidate.SelectedComponent.SignalInterval))
                .Distinct()
                .ToArray();
            if (!await _stager.StageAsync(
                    candidate.Symbol, boundary,
                    required.Select(interval => series[(candidate.Symbol, interval)]).ToArray(),
                    cancellationToken).ConfigureAwait(false))
            {
                unstaged.Add(candidate.ObservationId);
                rejectedObservations.Add(new PaperTrainingQualificationResult(
                    0, candidate.Symbol, false, 0m, 0, 0m,
                    candidate.ObservationId,
                    "Admission blocked: closed scanner evidence could not be verified in the durable worker candle store.",
                    candidate.StrategyId, false, candidate.Interval,
                    PaperTrainingCandidateDisposition.Rejected,
                    candidate.Agreement, candidate.RequiredAgreement, candidate.SignalCloseUtc));
                continue;
            }

            if (candidate.RankingUniverseSymbols is not null)
            {
                stagedDaily.Add(candidate.Symbol);
                var dailyAvailable = true;
                foreach (var symbol in candidate.RankingUniverseSymbols)
                {
                    if (stagedDaily.Contains(symbol))
                        continue;
                    if (!series.TryGetValue((symbol, CandleInterval.OneDay), out var daily)
                        || !await _stager.StageUniverseDailyAsync(symbol, boundary, [daily], cancellationToken)
                            .ConfigureAwait(false))
                    {
                        dailyAvailable = false;
                        break;
                    }
                    stagedDaily.Add(symbol);
                }
                if (!dailyAvailable)
                {
                    unstaged.Add(candidate.ObservationId);
                    rejectedObservations.Add(new PaperTrainingQualificationResult(
                        0, candidate.Symbol, false, 0m, 0, 0m, candidate.ObservationId,
                        "Admission blocked: the complete ranked-universe daily snapshot could not be verified in the durable worker candle store.",
                        candidate.StrategyId, false, candidate.Interval,
                        PaperTrainingCandidateDisposition.Rejected,
                        candidate.Agreement, candidate.RequiredAgreement, candidate.SignalCloseUtc));
                    continue;
                }
            }

            var template = PaperTrainingActivationService.ApprovedSlots
                .FirstOrDefault(slot => slot.StrategyId.Equals(candidate.StrategyId, StringComparison.Ordinal))
                ?? PaperTrainingActivationService.CreateApprovedWorkerTemplate(candidate.StrategyId);
            var slotNumber = strategyAssignments.Count > 0
                ? availableAssignments.First(assignment =>
                    assignment.StrategyId.Equals(candidate.StrategyId, StringComparison.Ordinal)).Slot
                : Enumerable.Range(1, ExperimentWorker.MaxWorkersPerUser)
                    .First(number => retainedSlots.All(slot => slot.Slot != number));
            maximum = await _admissionLimit.GetMaximumAsync(ownerId, _time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            if (maximum is < 0 or > ExperimentWorker.MaxWorkersPerUser)
                throw new InvalidOperationException("The paper entitlement has an invalid worker ceiling.");
            if (retainedSlots.Count >= maximum)
                break;
            retainedSlots.Add(template with
            {
                Slot = slotNumber,
                Symbol = candidate.Symbol,
                Interval = candidate.Interval,
                Seed = StableSeed(candidate.ObservationId),
                ProvenanceId = $"scan-{candidate.ObservationId[..24]}",
                StrategyParameters = strategyParameters.GetValueOrDefault(candidate.StrategyId, "{}"),
                StrategyVersion = _strategies.Definitions.Single(definition =>
                    definition.FamilyId == candidate.StrategyId).Version,
                PairFilters = candidate.PairFilters,
                SelectedComponent = candidate.SelectedComponent,
                RankingUniverseSymbols = candidate.RankingUniverseSymbols,
                AdmissionCloseUtc = candidate.SignalCloseUtc
            });
            activeKeys.Add((candidate.StrategyId, candidate.Symbol, candidate.Interval));
            symbolCounts[candidate.Symbol] = symbolCounts.GetValueOrDefault(candidate.Symbol) + 1;
            strategyCounts[candidate.StrategyId] = strategyCounts.GetValueOrDefault(candidate.StrategyId) + 1;
            if (strategyAssignments.Count > 0)
                availableAssignments.RemoveAll(assignment => assignment.Slot == slotNumber);
            admitted++;
        }

        maximum = await _admissionLimit.GetMaximumAsync(ownerId, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (maximum is < 0 or > ExperimentWorker.MaxWorkersPerUser)
            throw new InvalidOperationException("The paper entitlement has an invalid worker ceiling.");
        while (retainedSlots.Count > maximum && retainedSlots.Count > previouslyRetained)
            retainedSlots.RemoveAt(retainedSlots.Count - 1);
        admitted = retainedSlots.Count - previouslyRetained;
        var admittedIds = retainedSlots
            .Where(slot => slot.ProvenanceId.StartsWith("scan-", StringComparison.Ordinal))
            .Select(slot => slot.ProvenanceId[5..])
            .ToHashSet(StringComparer.Ordinal);
        string DeferredReason(RankedCandidate candidate)
        {
            if (retainedSlots.Count >= maximum)
                return maximum == 0
                    ? "No new paper-worker capacity is available; existing reservations remain for protection."
                    : $"All {maximum} allowed paper-worker slots are reserved.";
            if (activeKeys.Contains((candidate.StrategyId, candidate.Symbol, candidate.Interval)))
                return "This strategy, pair and interval already have a reserved paper worker.";
            if (symbolCounts.GetValueOrDefault(candidate.Symbol) >= 2)
                return "This pair already has two reserved paper workers.";
            if (strategyAssignments.Count > 0 && !availableAssignments.Any(assignment =>
                assignment.StrategyId.Equals(candidate.StrategyId, StringComparison.Ordinal)))
                return "No configured worker-strategy slot is available for this strategy.";
            return "The paper-worker reservation could not be retained at this scan boundary.";
        }
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
            .Concat(ranked.Where(candidate => !unstaged.Contains(candidate.ObservationId))
                .Select((candidate, rank) => new PaperTrainingQualificationResult(
                rank + 1,
                candidate.Symbol,
                admittedIds.Contains(candidate.ObservationId[..24]),
                candidate.Agreement * 20m,
                0,
                0m,
                candidate.ObservationId,
                admittedIds.Contains(candidate.ObservationId[..24])
                    ? $"Admitted from rank {rank + 1}. {candidate.Reason}"
                    : $"Queued at rank {rank + 1}: {DeferredReason(candidate)} {candidate.Reason}",
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
                CandleInterval.FiveMinutes,
                ScanMetrics: new PaperTrainingScanMetrics(
                    universe.Count, evaluated, ranked.Length, admitted, unstaged.Count)))
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
            Qualifications = observations,
            PendingUniverseSnapshot = universeSnapshot
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
            Math.Max(0, ranked.Length - admitted - unstaged.Count),
            admitted,
            persisted);
    }

    private async Task<IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries>> LoadSeriesAsync(
        IReadOnlyList<PaperTrainingUniverseCandidate> universe,
        DateTimeOffset boundary,
        IReadOnlyCollection<ApprovedExperimentStrategyDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var loaded = new ConcurrentDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries>();
        var requiredFamilies = definitions.Select(definition => definition.FamilyId)
            .Concat(definitions.Any(definition => definition.FamilyId == "platform.regime-switching-ensemble")
                ? s_regimeComponents : [])
            .Distinct(StringComparer.Ordinal);
        var requests = requiredFamilies
            .SelectMany(familyId => ApprovedConsensusStrategyProfiles.For(familyId)
                .Where(profile => IsEvaluationBoundary(
                    _strategies.Definitions.Single(definition => definition.FamilyId == familyId),
                    profile, boundary))
                .SelectMany(profile => ApprovedConsensusStrategyProfiles.RequiredIntervals(
                    familyId,
                    profile.Signal)))
            .Distinct()
            .SelectMany(interval => universe.Select(pair => (pair.Symbol, interval)));
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

    internal static bool IsEvaluationBoundary(
        ApprovedExperimentStrategyDefinition definition,
        ApprovedStrategyTimeframeProfile profile,
        DateTimeOffset boundary)
    {
        if (definition.FamilyId == "platform.relative-strength-pullback-rotation"
            && definition.Version >= 5)
            return boundary.UtcTicks % TimeSpan.FromHours(1).Ticks == 0
                && boundary.UtcTicks % TimeSpan.FromHours(4).Ticks != 0;
        return boundary.UtcTicks % TimeSpan.FromMinutes((int)profile.Signal).Ticks == 0;
    }

    internal static ExperimentAnalysisResult EvaluateCandidate(
            ApprovedExperimentStrategyRegistry strategies,
            ApprovedExperimentStrategyDefinition definition,
            PaperTrainingUniverseCandidate pair,
            ExperimentCandleSeries regime,
            ExperimentCandleSeries signal,
            ExperimentCandleSeries execution,
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
            string parametersJson,
            IReadOnlyDictionary<string, string> parametersByStrategy)
        {
            var familyId = definition.FamilyId;
            if (familyId == "platform.cross-sectional-momentum-rotation")
                return EvaluateCrossSectionalMomentum(pair, universe, allSeries, signal.AsOfUtc, parametersJson);
            if (familyId == "platform.relative-strength-pullback-rotation")
                return EvaluateRelativeStrengthPullback(strategies, pair, universe, allSeries,
                    execution.AsOfUtc, parametersJson);
            if (familyId == "platform.regime-switching-ensemble")
                return EvaluateRegimeConsensus(strategies, pair, universe, allSeries, regime, signal, parametersJson, parametersByStrategy);
            var hourlyContext = familyId == "platform.three-swing-channel-divergence"
                && allSeries.TryGetValue((pair.Symbol, CandleInterval.OneHour), out var hourly)
                    ? hourly
                    : null;
            return EvaluateDirectCandidate(strategies, definition, regime, signal, execution,
                parametersJson, hourlyContext);
        }

    internal static ExperimentAnalysisResult EvaluateDirectCandidate(
        ApprovedExperimentStrategyRegistry strategies,
        ApprovedExperimentStrategyDefinition definition,
        ExperimentCandleSeries regime,
        ExperimentCandleSeries signal,
        ExperimentCandleSeries execution,
        string parametersJson,
        ExperimentCandleSeries? hourlyContext = null)
    {
            ArgumentNullException.ThrowIfNull(strategies);
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.FamilyId is "platform.cross-sectional-momentum-rotation"
                or "platform.relative-strength-pullback-rotation"
                or "platform.regime-switching-ensemble")
                throw new ArgumentException(
                    "The family requires complete eligible-universe evidence, not a single-pair profile.",
                    nameof(definition));
            var familyId = definition.FamilyId;
            if (familyId == "platform.donchian-breakout-ensemble" && definition.Version >= 5
                && ApprovedStrategyParameters.TryNormalize(familyId, parametersJson,
                    out var normalized, out _)
                && ApprovedStrategyParameters.Read(familyId, normalized)
                    .String("donchianPlanModel") != "priorBreakRange")
                return Vetoed(familyId, "Legacy Donchian protection settings need owner review before new paper admissions");
            if (familyId == "platform.bollinger-mean-reversion" && definition.Version >= 5
                && ApprovedStrategyParameters.TryNormalize(familyId, parametersJson,
                    out var bollingerNormalized, out _)
                && ApprovedStrategyParameters.Read(familyId, bollingerNormalized)
                    .String("bollingerPlanModel") != "excursionMidBand")
                return Vetoed(familyId, "Legacy Bollinger protection settings need owner review before new paper admissions");
            if (familyId == "platform.rsi-pullback" && definition.Version >= 5
                && ApprovedStrategyParameters.TryNormalize(familyId, parametersJson,
                    out var rsiNormalized, out _)
                && ApprovedStrategyParameters.Read(familyId, rsiNormalized)
                    .String("rsiPlanModel") != "pullbackSwing")
                return Vetoed(familyId, "Legacy RSI pullback protection settings need owner review before new paper admissions");
            if (familyId == "platform.macd-volume" && definition.Version >= 5
                && ApprovedStrategyParameters.TryNormalize(familyId, parametersJson,
                    out var macdNormalized, out _)
                && ApprovedStrategyParameters.Read(familyId, macdNormalized)
                    .String("macdPlanModel") != "crossSwing")
                return Vetoed(familyId, "Legacy MACD protection settings need owner review before new paper admissions");
            if (familyId == "platform.ema-trend-continuation" && definition.Version >= 5
                && ApprovedStrategyParameters.TryNormalize(familyId, parametersJson,
                    out var emaNormalized, out _)
                && ApprovedStrategyParameters.Read(familyId, emaNormalized)
                    .String("emaPlanModel") != "pullbackSwing")
                return Vetoed(familyId, "Legacy EMA protection settings need owner review before new paper admissions");
            if (familyId == "platform.volatility-compression-breakout" && definition.Version >= 5
                && ApprovedStrategyParameters.TryNormalize(familyId, parametersJson,
                    out var compressionNormalized, out _)
                && ApprovedStrategyParameters.Read(familyId, compressionNormalized)
                    .String("compressionPlanModel") != "priorRange")
                return Vetoed(familyId, "Legacy compression protection settings need owner review before new paper admissions");
            if (familyId == "platform.three-swing-channel-divergence" && definition.Version >= 4
                && ApprovedStrategyParameters.TryNormalize(familyId, parametersJson,
                    out var swingNormalized, out _)
                && ApprovedStrategyParameters.Read(familyId, swingNormalized)
                    .String("threeSwingPlanModel") != "confirmedPivot")
                return Vetoed(familyId, "Legacy three-swing protection settings need owner review before new paper admissions");
            return strategies.EvaluateProfile(familyId, regime, signal, execution, parametersJson,
                hourlyContext, definition.Version);
    }

    private static ExperimentAnalysisResult EvaluateRegimeConsensus(
        ApprovedExperimentStrategyRegistry strategies,
        PaperTrainingUniverseCandidate pair,
        IReadOnlyList<PaperTrainingUniverseCandidate> universe,
        IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
        ExperimentCandleSeries regime,
        ExperimentCandleSeries signal,
        string parametersJson,
        IReadOnlyDictionary<string, string> parametersByStrategy)
    {
        const string family = "platform.regime-switching-ensemble";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        if (parameters.String("regimePlanModel") != "fourHourSwing")
            return Vetoed(family, "Legacy ensemble protection settings need owner review before new paper admissions");
        var ranked = RankUniverse(
            universe, allSeries, regime.AsOfUtc,
            parameters.Int32("momentumRankLookback"),
            parameters.Int32("trendRankLookback"),
            parameters.Int32("trendEma"));
        if (ranked.Length != universe.Count)
            return Vetoed(family, "complete point-in-time breadth evidence is unavailable");
        var classification = PaperRegimeClassifier.Classify(ranked, regime.Candles, parameters);
        if (classification.IsTrendDown)
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
        if (classification.BlockReason is not null)
            return Vetoed(family, classification.BlockReason);

        var passed = classification.EligibleFamilies
            .Select(mappedFamily => EvaluateMapped(
                strategies,
                mappedFamily,
                pair.Symbol,
                allSeries,
                parametersByStrategy.GetValueOrDefault(
                    mappedFamily,
                    ApprovedStrategyParameters.For(mappedFamily).DefaultsJson),
                regime.Interval, signal.Interval))
            .Where(component => component?.Analysis is { Outcome: ExperimentAnalysisOutcome.Analyzed, Value: > 0m })
            .ToArray();
        if (passed.Length != 1)
            return Vetoed(family, "the regime needs exactly one independently qualifying component; absent or ambiguous components block entry");
        var component = passed[0]!;
        if (component.Profile.Signal != signal.Interval)
            return Vetoed(family, "Selected component signal must close on the ensemble's four-hour protection boundary");
        var consensus = component.Analysis.Consensus;
        if (consensus is null)
            return Vetoed(family, "the selected component lacks an approved consensus record");
        var componentVersion = component.Version;
        var selection = new PaperRegimeComponentSelection(
            component.FamilyId, componentVersion, component.Parameters,
            universe.Select(item => item.Symbol).OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase).ToArray(),
            regime.AsOfUtc, signal.AsOfUtc, component.Profile.Signal,
            PaperRegimeComponentSelection.Fingerprint(component.Analysis));
        return ExperimentAnalysisResult.FromConsensus(
            family,
            consensus.Checks
                .Select(check => check with
                {
                    Rationale = $"{component.FamilyId} v{componentVersion}: {check.Rationale} Complete eligible-universe breadth is {classification.Breadth:P2}."
                })
                .ToArray(),
            consensus.RequiredAgreement,
            requiredEntryChecks: consensus.RequiredEntryChecks)
            .WithSelectedComponent(selection);
    }

    private static MappedComponent? EvaluateMapped(
        ApprovedExperimentStrategyRegistry strategies,
        string familyId,
        string symbol,
        IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
        string parametersJson,
        CandleInterval ensembleRegime,
        CandleInterval ensembleSignal)
    {
        MappedComponent? firstAvailable = null;
        foreach (var profile in ApprovedConsensusStrategyProfiles.For(familyId)
            .OrderByDescending(profile =>
                profile.Regime == ensembleRegime && profile.Signal == ensembleSignal)
            .ThenByDescending(profile => profile.Regime)
            .ThenByDescending(profile => profile.Signal)
            .ThenByDescending(profile => profile.Execution))
        {
            if (allSeries.TryGetValue((symbol, profile.Regime), out var regime)
                && allSeries.TryGetValue((symbol, profile.Signal), out var signal)
                && allSeries.TryGetValue((symbol, profile.Execution), out var execution))
            {
                // The v4 ensemble still uses its v1 ATR plan, not a v5 component's structural plan.
                var componentVersion = Math.Min(strategies.Definitions.Single(definition =>
                    definition.FamilyId == familyId).Version, 4);
                var mapped = new MappedComponent(familyId, componentVersion, profile, parametersJson,
                    strategies.EvaluateProfile(familyId, regime, signal, execution, parametersJson,
                        strategyVersion: componentVersion));
                if (mapped.Analysis is { Outcome: ExperimentAnalysisOutcome.Analyzed, Value: > 0m })
                    return mapped;
                firstAvailable ??= mapped;
            }
        }
        return firstAvailable;
    }

    private sealed record MappedComponent(
        string FamilyId, int Version, ApprovedStrategyTimeframeProfile Profile,
        string Parameters, ExperimentAnalysisResult Analysis);

    private static ExperimentAnalysisResult EvaluateCrossSectionalMomentum(
            PaperTrainingUniverseCandidate pair,
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
            DateTimeOffset asOfUtc,
            string parametersJson)
        {
            const string family = "platform.cross-sectional-momentum-rotation";
            var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
            if (parameters.String("momentumPlanModel") != "dailySwing")
                return Vetoed(family, "Legacy momentum protection settings need owner review before new paper admissions");
            var observations = RankUniverse(universe, allSeries, asOfUtc,
                    parameters.Int32("momentumRankLookback"),
                    parameters.Int32("trendRankLookback"),
                    parameters.Int32("trendEma"),
                    parameters.Int32("volatilityLookback"),
                    parameters.Int32("liquidityLookback"),
                    CrossSectionalConsensusRanking.ReadWeights(parameters, false))
                .OrderByDescending(observation => observation.MomentumScore)
                .ThenBy(observation => observation.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (observations.Length != universe.Count)
                return Vetoed(family, "the complete point-in-time daily universe is unavailable");
            var selected = observations.Single(observation =>
                observation.Symbol.Equals(pair.Symbol, StringComparison.OrdinalIgnoreCase));
            var topCount = Math.Max(1, (int)Math.Ceiling(observations.Length * parameters.Decimal("topRankPercent") / 100m));
            var top = Array.IndexOf(observations, selected) < topCount;
            var correlationSafe = selected.Symbol.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase)
                || decimal.Abs(selected.CorrelationToBenchmark) <= parameters.Decimal("maximumCorrelation");
            return ExperimentAnalysisResult.FromConsensus(
                family,
                [
                    SignalCheck("top-momentum-percentile", top, $"Instrument must rank in the top {parameters.Decimal("topRankPercent")}% of the complete eligible universe."),
                    SignalCheck("positive-absolute-return", selected.LongReturn > 0m, $"Configured {parameters.Int32("trendRankLookback")}-day absolute return must be positive."),
                    SignalCheck("daily-long-term-trend", selected.AboveEma200, $"Daily close must be above EMA {parameters.Int32("trendEma")}."),
                    SignalCheck("liquidity-and-history", selected.MedianQuoteVolume >= parameters.Decimal("minimumLiquidity"), "Point-in-time history and configured daily-liquidity requirements must pass."),
                    SignalCheck("volatility-and-correlation", selected.Volatility <= parameters.Decimal("maximumVolatility") && correlationSafe, "Configured volatility and XBT-correlation concentration limits must pass.")
                ],
                5);
        }

    private static ExperimentAnalysisResult EvaluateRelativeStrengthPullback(
            ApprovedExperimentStrategyRegistry strategies,
            PaperTrainingUniverseCandidate pair,
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
            DateTimeOffset asOfUtc,
            string parametersJson)
        {
            const string family = "platform.relative-strength-pullback-rotation";
            var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
            var revised = strategies.Definitions.Single(definition => definition.FamilyId == family).Version >= 4;
            if (revised && parameters.String("relativeRankingModel") != "dailyExcessBreadth")
                return Vetoed(family, "Legacy relative-strength ranking settings need owner review before new paper admissions");
            if (strategies.Definitions.Single(definition => definition.FamilyId == family).Version >= 5
                && parameters.String("relativePlanModel") != "fourHourStructure")
                return Vetoed(family, "Legacy relative-strength protection settings need owner review before new paper admissions");
            var observations = RankUniverse(universe, allSeries, asOfUtc,
                    parameters.Int32("momentumRankLookback"),
                    parameters.Int32("trendRankLookback"),
                    parameters.Int32("trendEma"),
                    parameters.Int32("volatilityLookback"),
                    parameters.Int32("liquidityLookback"),
                    CrossSectionalConsensusRanking.ReadWeights(parameters, true),
                    dailyExcessBreadth: revised)
                .OrderByDescending(observation => observation.RelativeStrengthScore)
                .ThenBy(observation => observation.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var minimumHistory = new[]
            {
                parameters.Int32("pullbackFastEma") + 1,
                parameters.Int32("pullbackSlowEma") + 1,
                parameters.Int32("atrPeriod") + 1,
                parameters.Int32("rsiPeriod") + 1
            }.Max();
            if (observations.Length != universe.Count
                || !allSeries.TryGetValue((pair.Symbol, CandleInterval.FourHours), out var setup)
                || !allSeries.TryGetValue((pair.Symbol, CandleInterval.OneHour), out var execution)
                || setup.Candles.Count < minimumHistory
                || execution.Candles.Count < 2)
                return Vetoed(family, "complete daily ranking, 4h setup, and 1h execution evidence is required");
            var laterConfirmation = strategies.Definitions.Single(definition =>
                definition.FamilyId == family).Version >= 5;
            if (laterConfirmation && (setup.AsOfUtc >= execution.AsOfUtc
                || execution.AsOfUtc > setup.AsOfUtc.AddHours(3)))
                return Vetoed(family, "a later closed one-hour candle must confirm the four-hour setup");

            var selected = observations.Single(observation =>
                observation.Symbol.Equals(pair.Symbol, StringComparison.OrdinalIgnoreCase));
            var topCount = Math.Max(1, (int)Math.Ceiling(observations.Length * parameters.Decimal("topRankPercent") / 100m));
            var top = Array.IndexOf(observations, selected) < topCount;
            var setupClose = setup.Candles[^1].Close;
            var ema20 = new ExponentialMovingAverageCalculator(parameters.Int32("pullbackFastEma")).Calculate(setup.Candles).Value!.Value;
            var ema50 = new ExponentialMovingAverageCalculator(parameters.Int32("pullbackSlowEma")).Calculate(setup.Candles).Value!.Value;
            var distance = Math.Min(decimal.Abs(setupClose - ema20), decimal.Abs(setupClose - ema50));
            var atr = new AverageTrueRangeCalculator(parameters.Int32("atrPeriod")).Calculate(setup.Candles).Value!.Value;
            var rsiPeriod = parameters.Int32("rsiPeriod");
            var rsi = new RelativeStrengthIndexCalculator(rsiPeriod).Calculate(setup.Candles).Value!.Value;
            var priorRsi = new RelativeStrengthIndexCalculator(rsiPeriod)
                .Calculate(setup.Candles.Take(setup.Candles.Count - 1).ToArray()).Value!.Value;
            return ExperimentAnalysisResult.FromConsensus(
                family,
                [
                    SignalCheck("top-relative-strength", top && (!revised || selected.BenchmarkExcessReturn > 0m),
                        $"Asset must rank in the top {parameters.Decimal("topRankPercent")}% and exceed same-quote XBT/EUR over the long daily lookback."),
                    SignalCheck("positive-daily-trend", selected.LongReturn > 0m && selected.AboveEma200
                        && selected.MedianQuoteVolume >= parameters.Decimal("minimumLiquidity")
                        && selected.Volatility <= parameters.Decimal("maximumVolatility"),
                        "Daily absolute trend, configured liquidity, and volatility limits must pass."),
                    SignalCheck("bounded-four-hour-pullback",
                        distance <= atr * parameters.Decimal("maximumPullbackAtr") && setupClose >= ema50,
                        $"Four-hour price must pull within {parameters.Decimal("maximumPullbackAtr")} ATR of configured EMA support without losing structure."),
                    SignalCheck("four-hour-rsi-cooled", rsi >= parameters.Decimal("pullbackRsiMinimum")
                        && rsi <= parameters.Decimal("pullbackRsiMaximum") && rsi >= priorRsi,
                        "Four-hour RSI must cool within the configured zone and then stop deteriorating."),
                    SignalCheck("one-hour-confirmation", execution.Candles[^1].Close > execution.Candles[^2].High,
                        laterConfirmation
                            ? "A later closed one-hour candle must confirm renewed upside after the four-hour setup."
                            : "A closed one-hour candle must confirm renewed upside.")
                ],
                5);
        }

    private static CrossSectionalRankEvidence[] RankUniverse(
            IReadOnlyList<PaperTrainingUniverseCandidate> universe,
            IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> allSeries,
            DateTimeOffset asOfUtc,
            int mediumLookback = 30,
            int longLookback = 90,
            int trendEmaPeriod = 200,
            int volatilityLookback = 90,
            int liquidityLookback = 30,
            CrossSectionalRankingWeights? weights = null,
            bool dailyExcessBreadth = false)
        {
            var daily = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);
            var requiredHistory = new[]
            {
                longLookback + 1, trendEmaPeriod + 1, volatilityLookback + 1, liquidityLookback
            }.Max();
            foreach (var pair in universe)
            {
                if (!allSeries.TryGetValue((pair.Symbol, CandleInterval.OneDay), out var series)
                    || (dailyExcessBreadth
                        ? series.AsOfUtc != AlignDown(asOfUtc, TimeSpan.FromDays(1))
                        : series.AsOfUtc > asOfUtc)
                    || series.Candles.Count < requiredHistory)
                {
                    return [];
                }
                daily.Add(pair.Symbol, series.Candles);
            }
            return CrossSectionalConsensusRanking.Evaluate(
                daily, mediumLookback, longLookback, trendEmaPeriod, volatilityLookback, liquidityLookback, weights,
                dailyExcessBreadth: dailyExcessBreadth);
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
        string Reason,
        PaperExchangeFilters PairFilters,
        PaperRegimeComponentSelection? SelectedComponent,
        IReadOnlyList<string>? RankingUniverseSymbols);
}
