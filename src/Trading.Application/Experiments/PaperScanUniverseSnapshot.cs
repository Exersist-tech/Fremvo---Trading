using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public sealed record PaperScanUniversePolicyEvidence(
    decimal MinimumMedianDailyQuoteVolume,
    int LiquidityLookbackDays,
    int MaximumCandidatePairs,
    IReadOnlyList<string> AllowedQuoteAssets,
    IReadOnlyList<string> ExcludedBaseAssets)
{
    public static PaperScanUniversePolicyEvidence Capture(PaperTrainingUniversePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        return new(policy.MinimumMedianDailyQuoteVolume, policy.LiquidityLookbackDays,
            policy.MaximumCandidatePairs,
            Array.AsReadOnly(policy.AllowedQuoteAssets.Order(StringComparer.OrdinalIgnoreCase).ToArray()),
            Array.AsReadOnly(policy.ExcludedBaseAssets.Order(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    public void Validate()
    {
        if (AllowedQuoteAssets is null || ExcludedBaseAssets is null
            || AllowedQuoteAssets.Any(string.IsNullOrWhiteSpace)
            || ExcludedBaseAssets.Any(string.IsNullOrWhiteSpace)
            || AllowedQuoteAssets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedQuoteAssets.Count
            || ExcludedBaseAssets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ExcludedBaseAssets.Count
            || !AllowedQuoteAssets.SequenceEqual(AllowedQuoteAssets.Order(StringComparer.OrdinalIgnoreCase))
            || !ExcludedBaseAssets.SequenceEqual(ExcludedBaseAssets.Order(StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Scanner universe policy evidence is incomplete or unordered.");
        new PaperTrainingUniversePolicy(MinimumMedianDailyQuoteVolume, LiquidityLookbackDays,
            MaximumCandidatePairs,
            AllowedQuoteAssets.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            ExcludedBaseAssets.ToFrozenSet(StringComparer.OrdinalIgnoreCase)).Validate();
    }
}

public sealed record PaperScanUniverseDailyEvidence(
    string Symbol,
    DateTimeOffset? AsOfUtc,
    string? CandleFingerprint);

public sealed record PaperScanSeriesEvidence(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset AsOfUtc,
    string CandleFingerprint);

public sealed record PaperScanStrategyEvidence(
    string FamilyId,
    int Version,
    string ParametersJson,
    bool Evaluated,
    bool ParametersValid = true);

/// <summary>
/// The complete candidate list observed by one scanner invocation, not a claim about
/// venue membership or data availability before the observation completed.
/// </summary>
public sealed record PaperScanUniverseSnapshot(
    Guid OwnerId,
    DateTimeOffset SignalBoundaryUtc,
    DateTimeOffset ObservedAtUtc,
    PaperScanUniversePolicyEvidence Policy,
    IReadOnlyList<PaperTrainingUniverseCandidate> Members,
    IReadOnlyList<PaperScanUniverseDailyEvidence> DailyEvidence,
    string Fingerprint,
    int EvidenceSchemaVersion = 1,
    IReadOnlyList<PaperScanSeriesEvidence>? SeriesEvidence = null,
    IReadOnlyList<PaperScanStrategyEvidence>? StrategyEvidence = null)
{
    public const int SchemaVersion = 1;
    public const int ExpandedSchemaVersion = 2;

    public static PaperScanUniverseSnapshot Capture(
        Guid ownerId,
        DateTimeOffset signalBoundaryUtc,
        DateTimeOffset observedAtUtc,
        IReadOnlyList<PaperTrainingUniverseCandidate> members,
        PaperTrainingUniversePolicy policy,
        IReadOnlyDictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries> series,
        IReadOnlyList<PaperScanStrategyEvidence>? strategies = null)
    {
        var policyEvidence = PaperScanUniversePolicyEvidence.Capture(policy);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(series);
        var copy = Array.AsReadOnly(members.ToArray());
        var daily = Array.AsReadOnly(copy.Select(member =>
        {
            if (!series.TryGetValue((member.Symbol, CandleInterval.OneDay), out var evidence))
                return new PaperScanUniverseDailyEvidence(member.Symbol, null, null);
            if (evidence.Candles.Count != ApprovedConsensusStrategyProfiles.RequiredHistory
                || evidence.AsOfUtc != evidence.Candles[^1].CloseTimeUtc
                || evidence.Candles.Any(candle => !candle.CanBeUsedForClosedCandleSignal)
                || evidence.Candles.Zip(evidence.Candles.Skip(1),
                    (left, right) => left.CloseTimeUtc == right.OpenTimeUtc).Any(equal => !equal))
                throw new ArgumentException("Loaded daily scanner evidence is incomplete or unsafe.", nameof(series));
            return new PaperScanUniverseDailyEvidence(member.Symbol, evidence.AsOfUtc,
                HistoricalCandleFingerprint.Compute(evidence.Candles));
        }).ToArray());
        ValidateEvidence(ownerId, signalBoundaryUtc, observedAtUtc, policyEvidence, copy, daily);
        if (strategies is not null)
        {
            var roles = Array.AsReadOnly(series
                .OrderBy(item => item.Key.Symbol, StringComparer.Ordinal)
                .ThenBy(item => item.Key.Interval)
                .Select(item =>
                {
                    ValidateSeries(item.Key.Symbol, item.Key.Interval, item.Value, signalBoundaryUtc);
                    return new PaperScanSeriesEvidence(item.Key.Symbol, item.Key.Interval,
                        item.Value.AsOfUtc, HistoricalCandleFingerprint.Compute(item.Value.Candles));
                }).ToArray());
            var saved = Array.AsReadOnly(strategies.ToArray());
            ValidateExpanded(signalBoundaryUtc, copy, daily, roles, saved);
            return new(ownerId, signalBoundaryUtc, observedAtUtc, policyEvidence, copy, daily,
                ComputeExpandedFingerprint(ownerId, signalBoundaryUtc, observedAtUtc,
                    policyEvidence, copy, daily, roles, saved),
                ExpandedSchemaVersion, roles, saved);
        }
        return new(ownerId, signalBoundaryUtc, observedAtUtc, policyEvidence, copy, daily,
            ComputeFingerprint(ownerId, signalBoundaryUtc, observedAtUtc, policyEvidence, copy, daily));
    }

    public void Validate()
    {
        ValidateEvidence(OwnerId, SignalBoundaryUtc, ObservedAtUtc, Policy, Members, DailyEvidence);
        string expected;
        if (EvidenceSchemaVersion == SchemaVersion
            && SeriesEvidence is null && StrategyEvidence is null)
            expected = ComputeFingerprint(OwnerId, SignalBoundaryUtc, ObservedAtUtc, Policy, Members, DailyEvidence);
        else if (EvidenceSchemaVersion == ExpandedSchemaVersion
            && SeriesEvidence is not null && StrategyEvidence is not null)
        {
            ValidateExpanded(SignalBoundaryUtc, Members, DailyEvidence, SeriesEvidence, StrategyEvidence);
            expected = ComputeExpandedFingerprint(OwnerId, SignalBoundaryUtc, ObservedAtUtc,
                Policy, Members, DailyEvidence, SeriesEvidence, StrategyEvidence);
        }
        else
            throw new InvalidOperationException("The scanner universe evidence schema is unsupported or incomplete.");
        if (!string.Equals(Fingerprint, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted scan universe does not match its content fingerprint.");
    }

    public void VerifySeriesWindow(ExperimentCandleSeries window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Validate();
        if (SeriesEvidence is null)
            throw new InvalidOperationException("This scan did not record complete timeframe evidence.");
        ValidateSeries(window.Symbol, window.Interval, window, SignalBoundaryUtc);
        var recorded = SeriesEvidence.SingleOrDefault(item =>
            item.Symbol == window.Symbol && item.Interval == window.Interval);
        if (recorded is null || recorded.AsOfUtc != window.AsOfUtc
            || recorded.CandleFingerprint != HistoricalCandleFingerprint.Compute(window.Candles))
            throw new InvalidOperationException("The closed timeframe window does not match the scanner's recorded input.");
    }

    public void VerifyDailyWindow(ExperimentCandleSeries window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Validate();
        var index = Enumerable.Range(0, Members.Count)
            .SingleOrDefault(candidate => string.Equals(
                Members[candidate].Symbol, window.Symbol, StringComparison.Ordinal), -1);
        if (index < 0 || DailyEvidence[index].CandleFingerprint is not { } fingerprint
            || window.Interval != CandleInterval.OneDay
            || window.AsOfUtc != DailyEvidence[index].AsOfUtc
            || window.Candles.Count != ApprovedConsensusStrategyProfiles.RequiredHistory
            || window.Candles[^1].CloseTimeUtc != window.AsOfUtc
            || window.Candles.Any(candle => !candle.CanBeUsedForClosedCandleSignal)
            || window.Candles.Zip(window.Candles.Skip(1),
                (left, right) => left.CloseTimeUtc == right.OpenTimeUtc).Any(equal => !equal)
            || !string.Equals(HistoricalCandleFingerprint.Compute(window.Candles),
                fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The complete closed daily window does not match what the scanner observed for this member.");
    }

    private static void ValidateEvidence(
        Guid ownerId,
        DateTimeOffset boundary,
        DateTimeOffset observedAt,
        PaperScanUniversePolicyEvidence policy,
        IReadOnlyList<PaperTrainingUniverseCandidate> members,
        IReadOnlyList<PaperScanUniverseDailyEvidence> dailyEvidence)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("An owner is required for scanner evidence.", nameof(ownerId));
        if (boundary == default || boundary.Offset != TimeSpan.Zero
            || boundary.UtcTicks % TimeSpan.FromMinutes(5).Ticks != 0
            || observedAt.Offset != TimeSpan.Zero || observedAt < boundary)
            throw new ArgumentException("Scanner evidence requires an aligned UTC boundary and a later UTC observation.");
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(dailyEvidence);
        if (members.Count > policy.MaximumCandidatePairs
            || members.Any(member => member is null
                || string.IsNullOrWhiteSpace(member.Symbol)
                || member.PairFilters is null
                || member.ObservedDays < policy.LiquidityLookbackDays
                || member.MinimumDailyQuoteVolume < 0m
                || member.MedianDailyQuoteVolume < member.MinimumDailyQuoteVolume
                || member.MedianDailyQuoteVolume < policy.MinimumMedianDailyQuoteVolume
                || !IsAllowedSymbol(member.Symbol, policy)
                || member.PairFilters.PriceTick <= 0m
                || member.PairFilters.QuantityStep <= 0m
                || member.PairFilters.MinimumQuantity <= 0m
                || member.PairFilters.MinimumNotional <= 0m)
            || members.Select(member => member.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Count
            || !members.SequenceEqual(members
                .OrderByDescending(member => member.MedianDailyQuoteVolume)
                .ThenBy(member => member.Symbol, StringComparer.OrdinalIgnoreCase))
            || dailyEvidence.Count != members.Count)
            throw new ArgumentException("The observed universe must contain at most 50 unique, ordered, valid Spot candidates.", nameof(members));
        var expectedDailyClose = new DateTimeOffset(
            boundary.UtcTicks - boundary.UtcTicks % TimeSpan.FromDays(1).Ticks, TimeSpan.Zero);
        for (var index = 0; index < members.Count; index++)
        {
            var daily = dailyEvidence[index];
            if (daily is null || !string.Equals(daily.Symbol, members[index].Symbol, StringComparison.Ordinal)
                || (daily.AsOfUtc is null) != (daily.CandleFingerprint is null)
                || (daily.AsOfUtc is not null && (daily.AsOfUtc != expectedDailyClose
                    || daily.CandleFingerprint is not { Length: 64 }
                    || daily.CandleFingerprint.Any(character =>
                        character is not (>= 'A' and <= 'F' or >= '0' and <= '9')))))
                throw new ArgumentException("Daily scanner evidence must be explicitly missing or match its member and completed UTC boundary.", nameof(dailyEvidence));
        }
    }

    private static bool IsAllowedSymbol(string symbol, PaperScanUniversePolicyEvidence policy)
    {
        var parts = symbol.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && !policy.ExcludedBaseAssets.Contains(parts[0], StringComparer.OrdinalIgnoreCase)
            && policy.AllowedQuoteAssets.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateExpanded(
        DateTimeOffset boundary,
        IReadOnlyList<PaperTrainingUniverseCandidate> members,
        IReadOnlyList<PaperScanUniverseDailyEvidence> daily,
        IReadOnlyList<PaperScanSeriesEvidence> roles,
        IReadOnlyList<PaperScanStrategyEvidence> strategies)
    {
        var symbols = members.Select(item => item.Symbol).ToHashSet(StringComparer.Ordinal);
        if (roles.Count > members.Count * 8 || strategies.Count is < 1 or > 11
            || strategies.Any(item => item is null
                || !ApprovedConsensusStrategyProfiles.TryGet(item.FamilyId, out _)
                || item.Version is < 1 or > 5
                || (item.ParametersValid
                    ? item.ParametersJson is null || item.ParametersJson.Length > 8_192
                        || !ApprovedStrategyParameters.TryNormalize(
                            item.FamilyId, item.ParametersJson, out var normalized, out _)
                        || normalized != item.ParametersJson
                    : item.ParametersJson is not { Length: 0 }))
            || strategies.Select(item => item.FamilyId).Distinct(StringComparer.Ordinal).Count()
                != strategies.Count
            || !strategies.SequenceEqual(strategies.OrderBy(item => item.FamilyId, StringComparer.Ordinal))
            || !strategies.Any(item => item.Evaluated)
            || roles.Any(item => item is null
                || !symbols.Contains(item.Symbol)
                || !Enum.IsDefined(item.Interval) || item.Interval == CandleInterval.None
                || item.AsOfUtc.Offset != TimeSpan.Zero
                || item.AsOfUtc != AlignDown(boundary, TimeSpan.FromMinutes((int)item.Interval))
                || item.CandleFingerprint is not { Length: 64 }
                || item.CandleFingerprint.Any(character =>
                    character is not (>= 'A' and <= 'F' or >= '0' and <= '9')))
            || roles.Select(item => (item.Symbol, item.Interval)).Distinct().Count() != roles.Count
            || !roles.SequenceEqual(roles.OrderBy(item => item.Symbol, StringComparer.Ordinal)
                .ThenBy(item => item.Interval))
            || daily.Any(item =>
                roles.Where(role => role.Symbol == item.Symbol && role.Interval == CandleInterval.OneDay)
                    .Any(role => role.AsOfUtc != item.AsOfUtc
                        || role.CandleFingerprint != item.CandleFingerprint))
            || daily.Any(item => item.CandleFingerprint is not null
                && !roles.Any(role => role.Symbol == item.Symbol && role.Interval == CandleInterval.OneDay)))
            throw new ArgumentException("Recorded scanner timeframes or strategy settings are invalid.");
    }

    private static void ValidateSeries(
        string symbol,
        CandleInterval interval,
        ExperimentCandleSeries window,
        DateTimeOffset boundary)
    {
        if (!Enum.IsDefined(interval) || interval == CandleInterval.None)
            throw new ArgumentException("Loaded scanner interval is not supported.");
        var expectedClose = AlignDown(boundary, TimeSpan.FromMinutes((int)interval));
        if (window is null || window.Symbol != symbol || window.Interval != interval
            || window.AsOfUtc != expectedClose
            || window.Candles.Count != ApprovedConsensusStrategyProfiles.RequiredHistory
            || window.Candles[^1].CloseTimeUtc != expectedClose
            || window.Candles.Any(candle => candle is null
                || candle.Symbol != symbol || candle.Interval != interval
                || !candle.CanBeUsedForClosedCandleSignal)
            || window.Candles.Zip(window.Candles.Skip(1),
                (left, right) => left.CloseTimeUtc == right.OpenTimeUtc).Any(equal => !equal))
            throw new ArgumentException("Loaded scanner timeframe evidence must be complete and closed.");
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan duration) =>
        new(value.UtcTicks - value.UtcTicks % duration.Ticks, TimeSpan.Zero);

    private static string ComputeExpandedFingerprint(
        Guid ownerId,
        DateTimeOffset boundary,
        DateTimeOffset observedAt,
        PaperScanUniversePolicyEvidence policy,
        IReadOnlyList<PaperTrainingUniverseCandidate> members,
        IReadOnlyList<PaperScanUniverseDailyEvidence> daily,
        IReadOnlyList<PaperScanSeriesEvidence> roles,
        IReadOnlyList<PaperScanStrategyEvidence> strategies) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = ExpandedSchemaVersion,
            OwnerId = ownerId,
            SignalBoundaryUtc = boundary,
            ObservedAtUtc = observedAt,
            Policy = policy,
            Members = members,
            DailyEvidence = daily,
            SeriesEvidence = roles,
            StrategyEvidence = strategies
        })));

    private static string ComputeFingerprint(
        Guid ownerId,
        DateTimeOffset boundary,
        DateTimeOffset observedAt,
        PaperScanUniversePolicyEvidence policy,
        IReadOnlyList<PaperTrainingUniverseCandidate> members,
        IReadOnlyList<PaperScanUniverseDailyEvidence> dailyEvidence) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion,
            OwnerId = ownerId,
            SignalBoundaryUtc = boundary,
            ObservedAtUtc = observedAt,
            Policy = policy,
            Members = members,
            DailyEvidence = dailyEvidence
        })));
}
