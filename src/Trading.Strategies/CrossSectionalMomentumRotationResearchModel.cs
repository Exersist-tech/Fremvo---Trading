using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>
/// A platform-authored cross-sectional momentum research model. It ranks a
/// complete, point-in-time universe and cannot create orders or allocations.
/// </summary>
public sealed class CrossSectionalMomentumRotationResearchModel : ApprovedStrategyTemplate, IStrategy
{
    private const string LookbackPeriodsParameter = "lookbackPeriods";
    private const string TopCountParameter = "topCount";

    private static readonly ReadOnlyCollection<StrategyParameterDefinition> s_parameterDefinitions =
        Array.AsReadOnly(
        [
            new StrategyParameterDefinition(LookbackPeriodsParameter, 2m, 365m, 90m, "Completed same-timeframe periods used for decimal momentum returns.", valueType: StrategyParameterValueType.WholeNumber),
            new StrategyParameterDefinition(TopCountParameter, 1m, 50m, 10m, "Maximum number of highest-ranked research observations to report.", valueType: StrategyParameterValueType.WholeNumber)
        ]);

    public CrossSectionalMomentumRotationResearchModel()
        : base(
            "cross-sectional-momentum-rotation-v1",
            "Cross-Sectional Momentum Rotation Research",
            TradingProductType.Spot,
            "Survivorship-aware decimal momentum ranking over a complete point-in-time universe.",
            "lookback periods: 2-365; top count: 1-50",
            "Every required universe member needs fresh membership/status evidence and safe, closed, same-timeframe history ending exactly at the UTC as-of time.",
            "Requires accepted rejection gates. Missing, stale, unavailable, delisted, unsafe, incomplete, incompatible, or future universe evidence blocks the entire ranking rather than excluding a member.")
    {
    }

    public StrategyTemplateId TemplateId { get; } = new("cross-sectional-momentum-rotation-v1");
    public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => s_parameterDefinitions;

    public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateGenericInput(input);

        return new StrategyAnalysisProposal(
            TemplateId,
            input.AsOfUtc,
            StrategyAnalysisDirection.Neutral,
            0m,
            "Unavailable: cross-sectional momentum rotation requires an immutable complete universe dataset and recorded rejection gates.");
    }

    public CrossSectionalMomentumResearchResult Evaluate(CrossSectionalMomentumRotationEvaluationInput evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ValidateParameters(evaluation.Parameters);

        var dataset = evaluation.Dataset;
        if (!evaluation.HasAcceptedGatesAt(dataset.AsOfUtc))
        {
            return CrossSectionalMomentumResearchResult.Blocked(
                dataset.AsOfUtc,
                "Blocked: rejection gates are missing, failed, or not aligned to the dataset UTC as-of time.");
        }

        var lookback = decimal.ToInt32(evaluation.Parameters.GetDecimal(LookbackPeriodsParameter));
        var topCount = decimal.ToInt32(evaluation.Parameters.GetDecimal(TopCountParameter));
        if (topCount > dataset.UniverseMembers.Count)
        {
            return CrossSectionalMomentumResearchResult.Blocked(
                dataset.AsOfUtc,
                string.Create(CultureInfo.InvariantCulture, $"Blocked: top count {topCount} exceeds the complete universe count {dataset.UniverseMembers.Count}."));
        }

        var observations = new List<CrossSectionalMomentumRankedInstrument>(dataset.UniverseMembers.Count);
        foreach (var member in dataset.UniverseMembers)
        {
            if (!member.IsMemberAtAsOf)
            {
                return Blocked(dataset, member.Symbol, "membership evidence does not confirm universe membership at as-of.");
            }

            if (member.Status is InstrumentTradingStatus.Unknown or InstrumentTradingStatus.Delisted
                || member.Status != InstrumentTradingStatus.Trading)
            {
                return Blocked(dataset, member.Symbol, $"status '{member.Status}' is unavailable or not fully trading.");
            }

            if (member.EvidenceObservedAtUtc != dataset.AsOfUtc)
            {
                return Blocked(dataset, member.Symbol, "membership/status evidence is stale or not aligned to the dataset as-of.");
            }

            if (member.ClosedCandles is null || member.ClosedCandles.Count < lookback + 1)
            {
                return Blocked(dataset, member.Symbol, string.Create(CultureInfo.InvariantCulture, $"requires {lookback + 1} completed candles."));
            }

            if (!TryValidateHistory(member, dataset, out var reason))
            {
                return Blocked(dataset, member.Symbol, reason);
            }

            var history = member.ClosedCandles;
            var start = history[history.Count - 1 - lookback].Close;
            var end = history[^1].Close;
            if (start <= 0m)
            {
                return Blocked(dataset, member.Symbol, "lookback starting close must be positive.");
            }

            observations.Add(new CrossSectionalMomentumRankedInstrument(
                member.InstrumentId,
                member.Symbol,
                (end / start) - 1m,
                0));
        }

        var ranked = observations
            .OrderByDescending(item => item.MomentumReturn)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .Select((item, index) => item with { Rank = index + 1 })
            .ToArray();
        return CrossSectionalMomentumResearchResult.Ranked(
            dataset.AsOfUtc,
            ranked.Take(topCount),
            string.Create(CultureInfo.InvariantCulture, $"Ranked {ranked.Length} complete universe members by {lookback}-period decimal momentum; top {topCount} are research observations only, not trade instructions."));
    }

    private static CrossSectionalMomentumResearchResult Blocked(
        CrossSectionalMomentumDataset dataset,
        string symbol,
        string detail) =>
        CrossSectionalMomentumResearchResult.Blocked(
            dataset.AsOfUtc,
            string.Create(CultureInfo.InvariantCulture, $"Blocked: required universe member '{symbol}' cannot be excluded: {detail}"));

    private static bool TryValidateHistory(
        CrossSectionalMomentumUniverseMember member,
        CrossSectionalMomentumDataset dataset,
        out string reason)
    {
        var history = member.ClosedCandles!;
        if (history.Count > CrossSectionalMomentumDataset.MaximumCandlesPerMember)
        {
            reason = "history exceeds the immutable dataset bound.";
            return false;
        }

        Candle? previous = null;
        foreach (var candle in history)
        {
            if (candle is null
                || !candle.IsClosed
                || !candle.CanBeUsedForClosedCandleSignal
                || candle.Interval != dataset.Interval
                || !string.Equals(candle.Symbol, member.Symbol, StringComparison.OrdinalIgnoreCase)
                || candle.CloseTimeUtc > dataset.AsOfUtc)
            {
                reason = "history is unsafe, incompatible, or contains future data.";
                return false;
            }

            if (previous is not null
                && (candle.OpenTimeUtc <= previous.OpenTimeUtc || candle.CloseTimeUtc <= previous.CloseTimeUtc))
            {
                reason = "history is not strictly chronological.";
                return false;
            }

            previous = candle;
        }

        if (history[^1].CloseTimeUtc != dataset.AsOfUtc)
        {
            reason = "history is stale and does not end at the dataset as-of.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private void ValidateParameters(StrategyParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Values.Count != s_parameterDefinitions.Count)
        {
            throw new ArgumentException("Cross-sectional momentum rotation accepts only its platform-defined parameters.", nameof(parameters));
        }

        foreach (var expected in ParameterDefinitions)
        {
            StrategyParameterDefinition actual;
            try { actual = parameters.GetDefinition(expected.Name); }
            catch (KeyNotFoundException exception) { throw new ArgumentException($"Cross-sectional momentum rotation requires parameter '{expected.Name}'.", nameof(parameters), exception); }

            if (actual.Minimum != expected.Minimum || actual.Maximum != expected.Maximum
                || actual.DefaultValue != expected.DefaultValue || actual.Required != expected.Required
                || actual.ValueType != expected.ValueType)
            {
                throw new ArgumentException($"Parameter '{expected.Name}' does not match platform-defined bounds and type.", nameof(parameters));
            }
        }
    }

    private void ValidateGenericInput(StrategyEvaluationInput input)
    {
        if (!TemplateId.Equals(input.TemplateId))
        {
            throw new ArgumentException("Evaluation input belongs to a different template.", nameof(input));
        }
    }
}

/// <summary>Immutable complete point-in-time universe input; it is not a selected-winners list.</summary>
public sealed class CrossSectionalMomentumDataset
{
    public const int MaximumCandlesPerMember = 366;

    public CrossSectionalMomentumDataset(
        string datasetId,
        DateTimeOffset asOfUtc,
        CandleInterval interval,
        IEnumerable<CrossSectionalMomentumUniverseMember> universeMembers)
    {
        if (string.IsNullOrWhiteSpace(datasetId))
        {
            throw new ArgumentException("An immutable dataset identifier is required.", nameof(datasetId));
        }

        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The dataset as-of timestamp must be explicit UTC.", nameof(asOfUtc));
        }

        StrategyTimeframeConfiguration.ValidateSupported(interval, nameof(interval));
        ArgumentNullException.ThrowIfNull(universeMembers);
        var copied = universeMembers.ToArray();
        if (copied.Length == 0 || copied.Any(member => member is null)
            || copied.Select(member => member.InstrumentId).Distinct().Count() != copied.Length
            || copied.Select(member => member.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count() != copied.Length)
        {
            throw new ArgumentException("A complete dataset requires unique non-null universe members.", nameof(universeMembers));
        }

        DatasetId = datasetId.Trim();
        AsOfUtc = asOfUtc;
        Interval = interval;
        UniverseMembers = new ReadOnlyCollection<CrossSectionalMomentumUniverseMember>(copied);
    }

    public string DatasetId { get; }
    public DateTimeOffset AsOfUtc { get; }
    public CandleInterval Interval { get; }
    public IReadOnlyList<CrossSectionalMomentumUniverseMember> UniverseMembers { get; }
}

/// <summary>Evidence for one required universe member, retained even when it is unavailable or delisted.</summary>
public sealed class CrossSectionalMomentumUniverseMember
{
    public CrossSectionalMomentumUniverseMember(
        Guid instrumentId,
        string symbol,
        bool isMemberAtAsOf,
        InstrumentTradingStatus status,
        DateTimeOffset evidenceObservedAtUtc,
        IReadOnlyList<Candle>? closedCandles)
    {
        if (instrumentId == Guid.Empty || string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("A platform instrument id and symbol are required.");
        }

        InstrumentId = instrumentId;
        Symbol = symbol.Trim();
        IsMemberAtAsOf = isMemberAtAsOf;
        Status = status;
        EvidenceObservedAtUtc = evidenceObservedAtUtc;
        ClosedCandles = closedCandles is null ? null : new ReadOnlyCollection<Candle>(closedCandles.ToArray());
    }

    public Guid InstrumentId { get; }
    public string Symbol { get; }
    public bool IsMemberAtAsOf { get; }
    public InstrumentTradingStatus Status { get; }
    public DateTimeOffset EvidenceObservedAtUtc { get; }
    public IReadOnlyList<Candle>? ClosedCandles { get; }
}

public sealed class CrossSectionalMomentumRotationEvaluationInput
{
    public CrossSectionalMomentumRotationEvaluationInput(
        CrossSectionalMomentumDataset dataset,
        StrategyParameterSet parameters,
        RejectionGateEvaluation? rejectionGates)
    {
        Dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        RejectionGates = rejectionGates;
    }

    public CrossSectionalMomentumDataset Dataset { get; }
    public StrategyParameterSet Parameters { get; }
    public RejectionGateEvaluation? RejectionGates { get; }

    internal bool HasAcceptedGatesAt(DateTimeOffset asOfUtc) =>
        RejectionGates is { Accepted: true, Results.Count: > 0 }
        && RejectionGates.Results.All(result => result.Status == RejectionGateStatus.Passed && result.EvaluatedAtUtc == asOfUtc);
}

public enum CrossSectionalMomentumResearchStatus { None = 0, Ranked = 1, Blocked = 2 }

public sealed record CrossSectionalMomentumRankedInstrument(Guid InstrumentId, string Symbol, decimal MomentumReturn, int Rank);

public sealed class CrossSectionalMomentumResearchResult
{
    private CrossSectionalMomentumResearchResult(
        CrossSectionalMomentumResearchStatus status,
        DateTimeOffset asOfUtc,
        IEnumerable<CrossSectionalMomentumRankedInstrument> rankings,
        string rationale)
    {
        Status = status;
        AsOfUtc = asOfUtc;
        Rankings = new ReadOnlyCollection<CrossSectionalMomentumRankedInstrument>(rankings.ToArray());
        Rationale = rationale;
    }

    public CrossSectionalMomentumResearchStatus Status { get; }
    public DateTimeOffset AsOfUtc { get; }
    public IReadOnlyList<CrossSectionalMomentumRankedInstrument> Rankings { get; }
    public string Rationale { get; }

    internal static CrossSectionalMomentumResearchResult Ranked(
        DateTimeOffset asOfUtc,
        IEnumerable<CrossSectionalMomentumRankedInstrument> rankings,
        string rationale) =>
        new(CrossSectionalMomentumResearchStatus.Ranked, asOfUtc, rankings, rationale);

    internal static CrossSectionalMomentumResearchResult Blocked(DateTimeOffset asOfUtc, string rationale) =>
        new(CrossSectionalMomentumResearchStatus.Blocked, asOfUtc, [], rationale);
}
