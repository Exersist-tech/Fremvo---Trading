using Trading.Domain.Market;
using Trading.Domain.Scanner;
using Trading.Indicators;
using Trading.MarketData;

namespace Trading.Application.Scanner;

public enum ScanCriterionOutcome
{
    None = 0,
    Passed = 1,
    Failed = 2,
    Unavailable = 3,
    Rejected = 4
}

public sealed record ScanCriterionEvidence(
    ScanCriterionKind Kind,
    ScanCriterionOutcome Outcome,
    decimal? ObservedValue,
    decimal Threshold,
    string Reason);

public sealed record ScanSymbolEvaluation(
    string Symbol,
    DateTimeOffset EvidenceAsOfUtc,
    IReadOnlyList<ScanCriterionEvidence> Criteria,
    ScanResult? Result,
    string? RejectionReason);

public sealed record ScanEvaluation(
    Guid ScanRunId,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<ScanSymbolEvaluation> Symbols)
{
    public IReadOnlyList<ScanResult> Results => Symbols
        .Where(symbol => symbol.Result is not null)
        .Select(symbol => symbol.Result!)
        .OrderBy(result => result.Rank)
        .ThenBy(result => result.Symbol, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Purely evaluates supplied, closed candle evidence. It never loads candles,
/// makes a request, or turns candidates into trading advice or permissions.
/// </summary>
public static class ScanCriteriaEvaluator
{
    public static ScanEvaluation Evaluate(
        ScanRequest request,
        Guid scanRunId,
        DateTimeOffset evidenceAsOfUtc,
        DateTimeOffset evaluatedAtUtc,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> candlesBySymbol)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candlesBySymbol);
        if (scanRunId == Guid.Empty)
        {
            throw new ArgumentException("A scan run id is required.", nameof(scanRunId));
        }

        var asOfUtc = evidenceAsOfUtc.ToUniversalTime();
        var evaluatedUtc = evaluatedAtUtc.ToUniversalTime();
        if (asOfUtc == default || evaluatedUtc == default || asOfUtc > evaluatedUtc)
        {
            throw new ArgumentException("Evidence must have a valid UTC evaluation timestamp.", nameof(evidenceAsOfUtc));
        }

        var evaluations = request.Symbols
            .Select(symbol => EvaluateSymbol(request, symbol, asOfUtc, candlesBySymbol))
            .OrderBy(evaluation => evaluation.Symbol, StringComparer.Ordinal)
            .ToArray();

        var qualifying = evaluations.Where(evaluation => evaluation.RejectionReason is null &&
                                                          evaluation.Criteria.All(criterion => criterion.Outcome == ScanCriterionOutcome.Passed))
            .OrderByDescending(evaluation => Score(evaluation.Criteria))
            .ThenBy(evaluation => evaluation.Symbol, StringComparer.Ordinal)
            .Take(request.ResultLimit)
            .ToArray();

        var rankBySymbol = qualifying
            .Select((evaluation, index) => new { evaluation.Symbol, Rank = index + 1 })
            .ToDictionary(item => item.Symbol, item => item.Rank, StringComparer.Ordinal);

        var finalized = evaluations.Select(evaluation =>
        {
            if (!rankBySymbol.TryGetValue(evaluation.Symbol, out var rank))
            {
                return evaluation;
            }

            var result = new ScanResult(
                request.OwnerId, request.Id, scanRunId, evaluation.Symbol, rank, Score(evaluation.Criteria),
                evaluation.Criteria.Where(criterion => criterion.Outcome == ScanCriterionOutcome.Passed).Select(criterion => criterion.Kind),
                asOfUtc, evaluatedUtc);
            return evaluation with { Result = result };
        }).ToArray();

        return new ScanEvaluation(scanRunId, evaluatedUtc, finalized);
    }

    private static ScanSymbolEvaluation EvaluateSymbol(
        ScanRequest request,
        string symbol,
        DateTimeOffset asOfUtc,
        IReadOnlyDictionary<string, IReadOnlyList<Candle>> candlesBySymbol)
    {
        if (!candlesBySymbol.TryGetValue(symbol, out var candles) || candles is null)
        {
            return Rejected(symbol, asOfUtc, request, "No supplied candle series exists for the requested symbol.");
        }

        var qualityError = ValidateHistory(symbol, request.Interval, asOfUtc, candles);
        if (qualityError is not null)
        {
            return Rejected(symbol, asOfUtc, request, qualityError);
        }

        var evidence = request.Criteria.Select(criterion => EvaluateCriterion(criterion, candles)).ToArray();
        return new ScanSymbolEvaluation(symbol, asOfUtc, evidence, null, null);
    }

    private static ScanSymbolEvaluation Rejected(
        string symbol, DateTimeOffset asOfUtc, ScanRequest request, string reason) =>
        new(symbol, asOfUtc, request.Criteria.Select(criterion =>
            new ScanCriterionEvidence(criterion.Kind, ScanCriterionOutcome.Rejected, null, criterion.Threshold, reason)).ToArray(),
            null, reason);

    private static string? ValidateHistory(
        string symbol, CandleInterval interval, DateTimeOffset asOfUtc, IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0)
        {
            return "No candle history was supplied.";
        }

        var duration = TimeSpan.FromMinutes((int)interval);
        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            if (candle is null || !string.Equals(candle.Symbol, symbol, StringComparison.OrdinalIgnoreCase) ||
                candle.Interval != interval || !candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal)
            {
                return "History contains a candle with incompatible identity or unsafe closed-candle evidence.";
            }

            if (candle.CloseTimeUtc > asOfUtc)
            {
                return "History contains future evidence after the requested as-of time.";
            }

            if (candle.CloseTimeUtc - candle.OpenTimeUtc != duration)
            {
                return "History contains a candle whose duration does not match the requested interval.";
            }

            if (index > 0 && (candle.OpenTimeUtc != candles[index - 1].CloseTimeUtc ||
                              candle.OpenTimeUtc <= candles[index - 1].OpenTimeUtc))
            {
                return "History is missing, duplicate, or out of chronological order.";
            }
        }

        return candles[^1].CloseTimeUtc == asOfUtc
            ? null
            : "History is stale because its latest closed candle does not equal the requested as-of time.";
    }

    private static ScanCriterionEvidence EvaluateCriterion(ScanCriterion criterion, IReadOnlyList<Candle> candles) =>
        criterion.Kind switch
        {
            ScanCriterionKind.MinimumClosedCandleCount => Compare(
                criterion, candles.Count, candles.Count >= criterion.Threshold, "closed candle count"),
            ScanCriterionKind.MinimumCandleVolume => Compare(
                criterion, candles[^1].Volume, candles[^1].Volume >= criterion.Threshold, "latest closed-candle volume"),
            ScanCriterionKind.MaximumCandleRangePercent => EvaluateRange(criterion, candles[^1]),
            ScanCriterionKind.CloseAboveSimpleMovingAverage => EvaluateSma(criterion, candles),
            _ => new ScanCriterionEvidence(criterion.Kind, ScanCriterionOutcome.Rejected, null, criterion.Threshold, "Unsupported criterion.")
        };

    private static ScanCriterionEvidence EvaluateSma(ScanCriterion criterion, IReadOnlyList<Candle> candles)
    {
        var sma = new SimpleMovingAverageCalculator(decimal.ToInt32(criterion.Threshold)).Calculate(candles);
        if (!sma.IsReady)
        {
            return new ScanCriterionEvidence(criterion.Kind, ScanCriterionOutcome.Unavailable, null, criterion.Threshold,
                $"Simple moving average requires {sma.RequiredCandleCount} closed candles; {sma.AvailableCandleCount} supplied.");
        }

        return Compare(criterion, sma.Value!.Value, candles[^1].Close > sma.Value.Value,
            $"latest close ({candles[^1].Close}) above simple moving average");
    }

    private static ScanCriterionEvidence EvaluateRange(ScanCriterion criterion, Candle candle)
    {
        if (candle.Close == 0m)
        {
            return new ScanCriterionEvidence(criterion.Kind, ScanCriterionOutcome.Unavailable, null, criterion.Threshold,
                "Candle range percentage is unavailable when close is zero.");
        }

        var range = ((candle.High - candle.Low) / candle.Close) * 100m;
        return Compare(criterion, range, range <= criterion.Threshold, "latest closed-candle range percent");
    }

    private static ScanCriterionEvidence Compare(ScanCriterion criterion, decimal observed, bool passed, string description) =>
        new(criterion.Kind, passed ? ScanCriterionOutcome.Passed : ScanCriterionOutcome.Failed, observed, criterion.Threshold,
            $"{description} {(passed ? "met" : "did not meet")} threshold {criterion.Threshold}.");

    private static decimal Score(IReadOnlyList<ScanCriterionEvidence> criteria) =>
        criteria.Count == 0 ? 0m : (decimal)criteria.Count(criterion => criterion.Outcome == ScanCriterionOutcome.Passed) / criteria.Count;
}
