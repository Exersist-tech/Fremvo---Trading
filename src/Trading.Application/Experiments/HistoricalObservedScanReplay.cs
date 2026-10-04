using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public sealed record HistoricalObservedScanReplayRequest(
    PaperScanUniverseSnapshot Snapshot,
    IReadOnlyList<HistoricalPaperReplaySeries> Archives,
    string FamilyId,
    string Symbol);

public sealed record HistoricalObservedScanRole(
    string Symbol,
    CandleInterval Interval,
    HistoricalDataset Manifest,
    DateTimeOffset WindowAsOfUtc,
    string WindowFingerprint);

/// <summary>
/// A reconstructed scanner decision at one observed boundary. This has no
/// executable quote, worker state, fill, exit, or historical return.
/// </summary>
public sealed record HistoricalObservedScanReplayResult(
    string FamilyId,
    int StrategyVersion,
    string Symbol,
    string NormalizedParametersJson,
    Guid OwnerId,
    DateTimeOffset SignalBoundaryUtc,
    DateTimeOffset ObservedAtUtc,
    string SnapshotFingerprint,
    IReadOnlyList<HistoricalObservedScanRole> MatchedInputs,
    ExperimentAnalysisResult Analysis)
{
    public string EvidenceScope { get; } = "observed-boundary-scanner-signal-only";
}

/// <summary>
/// Reuses the paper scanner's ranking and ensemble evaluator with only
/// fingerprint-matched, immutable inputs from one recorded scanner invocation.
/// </summary>
public sealed class HistoricalObservedScanReplay(ApprovedExperimentStrategyRegistry strategies)
{
    private readonly ApprovedExperimentStrategyRegistry _strategies =
        strategies ?? throw new ArgumentNullException(nameof(strategies));

    public HistoricalObservedScanReplayResult Run(
        HistoricalObservedScanReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = request.Snapshot;
        Preflight(snapshot, request.FamilyId, request.Symbol);
        var pinned = snapshot.StrategyEvidence!;
        var saved = pinned.Single(item => item.FamilyId == request.FamilyId);
        var pair = snapshot.Members.Single(item => item.Symbol == request.Symbol);
        var definition = _strategies.ResolveDefinition(saved.FamilyId, saved.Version);
        var profile = ApprovedConsensusStrategyProfiles.For(request.FamilyId).Single();

        var windows = HistoricalPaperUniverseEvidence.VerifyContext(
            snapshot, request.Archives, cancellationToken);
        if (snapshot.Members.Any(member =>
                !windows.ContainsKey((member.Symbol, CandleInterval.OneDay)))
            || !windows.TryGetValue((pair.Symbol, profile.Regime), out var regime)
            || !windows.TryGetValue((pair.Symbol, profile.Signal), out var signal)
            || !windows.TryGetValue((pair.Symbol, profile.Execution), out var execution))
            throw new InvalidOperationException(
                "Complete daily ranking and all required candidate timeframes must have been recorded.");

        var parametersByStrategy = pinned.ToDictionary(item => item.FamilyId,
            item => item.ParametersJson, StringComparer.Ordinal);
        var analysis = ContinuousPaperOpportunityScanner.EvaluateCandidate(
            _strategies, definition, pair, regime, signal, execution,
            snapshot.Members, windows, saved.ParametersJson, parametersByStrategy);
        var manifests = request.Archives.ToDictionary(
            item => item.Dataset.VersionIdentity, item => item.Dataset, StringComparer.Ordinal);
        var inputs = snapshot.SeriesEvidence!.Select(item =>
        {
            var window = windows[(item.Symbol, item.Interval)];
            return new HistoricalObservedScanRole(item.Symbol, item.Interval,
                manifests[window.DatasetProvenance], window.AsOfUtc,
                HistoricalCandleFingerprint.Compute(window.Candles));
        }).ToArray();
        return new(request.FamilyId, saved.Version, pair.Symbol, saved.ParametersJson,
            snapshot.OwnerId, snapshot.SignalBoundaryUtc, snapshot.ObservedAtUtc,
            snapshot.Fingerprint, Array.AsReadOnly(inputs), analysis);
    }

    public void Preflight(PaperScanUniverseSnapshot snapshot, string familyId, string symbol)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        snapshot.Validate();
        if (familyId is not ("platform.cross-sectional-momentum-rotation"
            or "platform.relative-strength-pullback-rotation"
            or "platform.regime-switching-ensemble"))
            throw new ArgumentException("This replay accepts only the three universe-dependent families.",
                nameof(familyId));
        var pinned = snapshot.StrategyEvidence
            ?? throw new InvalidOperationException("The scan has no pinned strategy settings.");
        var definitions = _strategies.Definitions;
        if (pinned.Count != definitions.Count
            || pinned.Any(item => !item.ParametersValid
                || !definitions.Any(definition =>
                    definition.FamilyId == item.FamilyId && definition.Version == item.Version)))
            throw new InvalidOperationException(
                "Complete approved settings and the exact scanner strategy versions are required.");
        var saved = pinned.Single(item => item.FamilyId == familyId);
        if (!saved.Evaluated)
            throw new InvalidOperationException("The scanner did not evaluate this family at this boundary.");
        if (!snapshot.Members.Any(item => item.Symbol == symbol))
            throw new ArgumentException("The pair was not in this observed scanner universe.",
                nameof(symbol));
        var definition = _strategies.ResolveDefinition(saved.FamilyId, saved.Version);
        var profile = ApprovedConsensusStrategyProfiles.For(familyId).Single();
        if (!ContinuousPaperOpportunityScanner.IsEvaluationBoundary(
                definition, profile, snapshot.SignalBoundaryUtc))
            throw new InvalidOperationException("The family was not evaluated at this signal boundary.");
        var roles = snapshot.SeriesEvidence!
            .Select(item => (item.Symbol, item.Interval)).ToHashSet();
        if (snapshot.Members.Any(member => !roles.Contains((member.Symbol, CandleInterval.OneDay)))
            || !roles.Contains((symbol, profile.Regime))
            || !roles.Contains((symbol, profile.Signal))
            || !roles.Contains((symbol, profile.Execution)))
            throw new InvalidOperationException(
                "Complete daily ranking and the candidate's required timeframes were not recorded.");
    }
}
