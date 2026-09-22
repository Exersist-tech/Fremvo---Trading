using System.Collections.Concurrent;
using System.Collections.Frozen;
using Trading.Domain.Market;
using Trading.Domain.Universe;
using Trading.MarketData;

namespace Trading.Application.Experiments;

public sealed record PaperTrainingUniversePolicy(
    decimal MinimumMedianDailyQuoteVolume,
    int LiquidityLookbackDays,
    int MaximumCandidatePairs,
    IReadOnlySet<string> AllowedQuoteAssets,
    IReadOnlySet<string> ExcludedBaseAssets)
{
    public static PaperTrainingUniversePolicy PlatformDefault { get; } = new(
        1_000_000m,
        30,
        40,
        new[] { "EUR" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        new[] { "USD", "EUR", "GBP", "CAD", "JPY", "CHF", "AUD", "USDT", "USDC", "DAI", "PYUSD" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    public PaperTrainingUniversePolicy Validate()
    {
        if (MinimumMedianDailyQuoteVolume < PlatformDefault.MinimumMedianDailyQuoteVolume)
            throw new ArgumentOutOfRangeException(nameof(MinimumMedianDailyQuoteVolume),
                $"Median daily quote volume cannot be below {PlatformDefault.MinimumMedianDailyQuoteVolume}.");
        if (LiquidityLookbackDays < PlatformDefault.LiquidityLookbackDays)
            throw new ArgumentOutOfRangeException(nameof(LiquidityLookbackDays),
                $"Liquidity lookback cannot be shorter than {PlatformDefault.LiquidityLookbackDays} days.");
        if (MaximumCandidatePairs is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(MaximumCandidatePairs),
                "Candidate-pair count must be between one and fifty.");
        if (AllowedQuoteAssets.Count == 0)
            throw new ArgumentException("At least one quote asset is required.", nameof(AllowedQuoteAssets));
        return this;
    }
}

public sealed record PaperTrainingUniverseCandidate(
    string Symbol,
    decimal MedianDailyQuoteVolume,
    decimal MinimumDailyQuoteVolume,
    int ObservedDays);

/// <summary>
/// Discovers a bounded, point-in-time Kraken-neutral paper universe from active Spot pairs and
/// closed daily liquidity evidence that predates strategy validation.
/// </summary>
public sealed class PaperTrainingUniverseDiscovery
{
    private readonly ITradablePairSource _pairs;
    private readonly IHistoricalCandleSource _candles;
    private readonly PaperTrainingUniversePolicy _policy;

    public PaperTrainingUniverseDiscovery(
        ITradablePairSource pairs,
        IHistoricalCandleSource candles,
        PaperTrainingUniversePolicy policy)
    {
        _pairs = pairs ?? throw new ArgumentNullException(nameof(pairs));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Validate();
    }

    public async Task<IReadOnlyList<PaperTrainingUniverseCandidate>> DiscoverAsync(
        DateTimeOffset evidenceAsOfUtc,
        CancellationToken cancellationToken = default)
    {
        if (evidenceAsOfUtc.Offset != TimeSpan.Zero || evidenceAsOfUtc > DateTimeOffset.UtcNow)
            throw new ArgumentException("Universe evidence time must be a completed UTC instant.", nameof(evidenceAsOfUtc));

        var tradable = await _pairs.ListAsync(cancellationToken).ConfigureAwait(false);
        var eligiblePairs = tradable
            .Where(IsEligibleSpotPair)
            .GroupBy(pair => pair.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var candidates = new ConcurrentBag<PaperTrainingUniverseCandidate>();
        var fromUtc = evidenceAsOfUtc.AddDays(-_policy.LiquidityLookbackDays - 2);

        await Parallel.ForEachAsync(
            eligiblePairs,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 2 },
            async (pair, token) =>
            {
                var daily = (await _candles
                        .FetchAsync(pair.DisplayName, CandleInterval.OneDay, fromUtc, token)
                        .ConfigureAwait(false))
                    .Where(candle => candle.CloseTimeUtc <= evidenceAsOfUtc
                        && candle.CanBeUsedForClosedCandleSignal
                        && candle.Close > 0m)
                    .OrderByDescending(candle => candle.CloseTimeUtc)
                    .Take(_policy.LiquidityLookbackDays)
                    .OrderBy(candle => candle.OpenTimeUtc)
                    .ToArray();
                if (!HasCompleteDailyEvidence(daily))
                    return;

                var quoteVolumes = daily.Select(candle => candle.Volume * candle.Close).ToArray();
                var median = InstrumentMetrics.Median(quoteVolumes);
                if (median < _policy.MinimumMedianDailyQuoteVolume)
                    return;

                candidates.Add(new(
                    pair.DisplayName,
                    median,
                    quoteVolumes.Min(),
                    daily.Length));
            }).ConfigureAwait(false);

        return candidates
            .OrderByDescending(candidate => candidate.MedianDailyQuoteVolume)
            .ThenBy(candidate => candidate.Symbol, StringComparer.OrdinalIgnoreCase)
            .Take(_policy.MaximumCandidatePairs)
            .ToArray();
    }

    private bool IsEligibleSpotPair(TradablePair pair)
    {
        if (!pair.IsActive || pair.DisplayName.Contains('.', StringComparison.Ordinal))
            return false;

        var parts = pair.DisplayName.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && !_policy.ExcludedBaseAssets.Contains(parts[0])
            && _policy.AllowedQuoteAssets.Contains(parts[1]);
    }

    private bool HasCompleteDailyEvidence(Candle[] daily)
    {
        if (daily.Length != _policy.LiquidityLookbackDays
            || daily.Select(candle => candle.OpenTimeUtc).Distinct().Count() != daily.Length)
            return false;

        return !daily.Zip(
                daily.Skip(1),
                static (left, right) => left.CloseTimeUtc == right.OpenTimeUtc)
            .Any(static consecutive => !consecutive);
    }
}

public sealed record PaperTrainingAutoSelectionRequest(
    decimal StartingCash,
    IReadOnlyList<CandleInterval> Intervals,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    PaperTrainingQualificationGate Gate);

public sealed record PaperTrainingAutoSelection(
    IReadOnlyList<PaperTrainingWorkerSlot> Slots,
    IReadOnlyList<PaperTrainingQualificationResult> Qualifications,
    IReadOnlyList<PaperTrainingUniverseCandidate> Universe);

/// <summary>
/// Selects on validation evidence and confirms the selected finalists on untouched holdout
/// evidence. Qualification status remains explicit; unqualified finalists may continue only as
/// fake-funds paper exploration and cannot acquire live eligibility.
/// </summary>
public sealed class PaperTrainingAutoSelectionService
{
    private const int MaximumQualificationCandidates = 250;
    private const int QualificationCandleCount = 600;
    private const int ValidationCandleCount = 420;
    public const int MaximumWorkersPerStrategy = 1;
    public static IReadOnlyList<CandleInterval> ApprovedIntervals { get; } =
        Array.AsReadOnly(new[]
        {
            CandleInterval.FiveMinutes,
            CandleInterval.FifteenMinutes,
            CandleInterval.ThirtyMinutes,
            CandleInterval.OneHour
        });
    private readonly PaperTrainingUniverseDiscovery _universe;
    private readonly PaperTrainingHistoricalQualification _qualification;

    public PaperTrainingAutoSelectionService(
        PaperTrainingUniverseDiscovery universe,
        PaperTrainingHistoricalQualification qualification)
    {
        _universe = universe ?? throw new ArgumentNullException(nameof(universe));
        _qualification = qualification ?? throw new ArgumentNullException(nameof(qualification));
    }

    public async Task<PaperTrainingAutoSelection> SelectAsync(
        PaperTrainingAutoSelectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Gate.Validate();
        if (request.StartingCash <= 0m)
            throw new ArgumentOutOfRangeException(nameof(request), "Fake starting cash must be positive.");
        if (request.Intervals is null
            || request.Intervals.Count == 0
            || request.Intervals.Distinct().Count() != request.Intervals.Count
            || request.Intervals.Any(interval => !ApprovedIntervals.Contains(interval)))
            throw new ArgumentOutOfRangeException(nameof(request), "Automatic paper selection requires distinct approved 5, 15, 30, or 60 minute intervals.");
        if (request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero
            || request.ToUtc <= request.FromUtc || request.ToUtc > DateTimeOffset.UtcNow)
            throw new ArgumentException("Automatic selection requires a completed UTC historical range.", nameof(request));
        var duration = request.ToUtc - request.FromUtc;
        if (duration < TimeSpan.FromDays(10) || duration > TimeSpan.FromDays(40))
            throw new ArgumentOutOfRangeException(nameof(request),
                "The liquidity evidence cutoff must be between ten and forty days before the completed-candle endpoint.");

        var universe = await _universe.DiscoverAsync(request.FromUtc, cancellationToken).ConfigureAwait(false);
        if (universe.Count == 0)
            throw new InvalidOperationException("No active Kraken Spot pair met the platform liquidity and data-quality gates.");

        var templates = PaperTrainingActivationService.ApprovedSlots
            .Where(slot => PaperTrainingHistoricalQualification.Supports(slot.StrategyId))
            .GroupBy(slot => slot.StrategyId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (templates.Length == 0)
            throw new InvalidOperationException("No approved strategy has a historical paper evaluator.");
        var maximumPairs = Math.Max(1, MaximumQualificationCandidates / (templates.Length * request.Intervals.Count));
        var candidates = universe
            .Take(maximumPairs)
            .SelectMany(pair => templates.SelectMany(template => request.Intervals.Select(interval => template with
            {
                Slot = 0,
                Symbol = pair.Symbol,
                StartingCash = request.StartingCash,
                Seed = StableSeed(template.StrategyId, pair.Symbol, interval),
                Interval = interval
            })))
            .Take(MaximumQualificationCandidates)
            .Select((slot, index) => slot with { Slot = index + 1 })
            .ToArray();

        var validation = await QualifyByIntervalAsync(
            candidates,
            request.ToUtc,
            validation: true,
            request.Gate,
            cancellationToken).ConfigureAwait(false);
        var validationBySlot = validation.ToDictionary(result => result.Slot);
        var finalists = SelectDiverseCandidates(candidates, validationBySlot)
            .Select((slot, index) => slot with { Slot = index + 1 })
            .ToArray();

        var holdout = await QualifyByIntervalAsync(
            finalists,
            request.ToUtc,
            validation: false,
            request.Gate,
            cancellationToken).ConfigureAwait(false);
        var validationLookup = validation.ToDictionary(
            result =>
            {
                var slot = candidates.Single(candidate => candidate.Slot == result.Slot);
                return (slot.StrategyId, result.Symbol, slot.Interval);
            },
            result => result);
        var combined = holdout.Select(result =>
        {
            var slot = finalists.Single(candidate => candidate.Slot == result.Slot);
            var prior = validationLookup[(slot.StrategyId, slot.Symbol, slot.Interval)];
            var accepted = prior.Accepted && result.Accepted;
            return result with
            {
                Accepted = accepted,
                PaperOnlyExploration = !accepted,
                Reason = accepted
                    ? $"Validation and untouched holdout passed. Holdout: {result.Reason}"
                    : $"Unqualified paper exploration only. Validation: {prior.Reason} Holdout: {result.Reason}",
                DatasetFingerprint = $"{prior.DatasetFingerprint}:{result.DatasetFingerprint}"
            };
        }).ToArray();
        return new(finalists, combined, universe);
    }

    private async Task<IReadOnlyList<PaperTrainingQualificationResult>> QualifyByIntervalAsync(
        PaperTrainingWorkerSlot[] candidates,
        DateTimeOffset toUtc,
        bool validation,
        PaperTrainingQualificationGate gate,
        CancellationToken cancellationToken)
    {
        var results = new List<PaperTrainingQualificationResult>(candidates.Length);
        foreach (var intervalGroup in candidates.GroupBy(slot => slot.Interval))
        {
            var duration = TimeSpan.FromMinutes((int)intervalGroup.Key);
            var rangeStart = toUtc.AddTicks(-duration.Ticks * QualificationCandleCount);
            var splitUtc = rangeStart.AddTicks(duration.Ticks * ValidationCandleCount);
            results.AddRange(await _qualification.RunAsync(
                new(
                    intervalGroup.ToArray(),
                    intervalGroup.Key,
                    validation ? rangeStart : splitUtc,
                    validation ? splitUtc : toUtc,
                    gate),
                cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private static List<PaperTrainingWorkerSlot> SelectDiverseCandidates(
        IEnumerable<PaperTrainingWorkerSlot> candidates,
        Dictionary<int, PaperTrainingQualificationResult> results)
    {
        var ranked = candidates
            .OrderByDescending(slot => results[slot.Slot].NetReturnPercent)
            .ThenBy(slot => results[slot.Slot].MaximumDrawdownPercent)
            .ThenBy(slot => slot.StrategyId, StringComparer.Ordinal)
            .ThenBy(slot => slot.Interval)
            .ThenBy(slot => slot.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select((slot, rank) => (Slot: slot, Rank: rank))
            .ToList();
        var selected = new List<PaperTrainingWorkerSlot>(PaperTrainingActivationService.MaximumSlots);
        var strategyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var intervals = new HashSet<CandleInterval>();
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (selected.Count < PaperTrainingActivationService.MaximumSlots && ranked.Count > 0)
        {
            var next = ranked
                .Where(candidate => !strategyCounts.TryGetValue(candidate.Slot.StrategyId, out var count)
                    || count < MaximumWorkersPerStrategy)
                .OrderByDescending(candidate =>
                    (intervals.Contains(candidate.Slot.Interval) ? 0 : 2)
                    + (symbols.Contains(candidate.Slot.Symbol) ? 0 : 1))
                .ThenBy(candidate => candidate.Rank)
                .FirstOrDefault();
            if (next.Slot is null)
                break;
            selected.Add(next.Slot);
            strategyCounts[next.Slot.StrategyId] =
                strategyCounts.GetValueOrDefault(next.Slot.StrategyId) + 1;
            intervals.Add(next.Slot.Interval);
            symbols.Add(next.Slot.Symbol);
            ranked.Remove(next);
        }

        return selected;
    }

    private static int StableSeed(string strategyId, string symbol, CandleInterval interval)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{strategyId}|{symbol}|{(int)interval}"));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }
}
