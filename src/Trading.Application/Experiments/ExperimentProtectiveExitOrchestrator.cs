using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public enum ExperimentProtectiveExitKind { StopLoss = 0, TakeProfit, MomentumReversal }

/// <summary>
/// Owner- and worker-scoped paper-position evidence. Implementations must obtain this from the
/// experiment's durable state; this is intentionally not a mutable position command.
/// </summary>
public sealed record ExperimentProtectiveExitPosition(
    Guid UserId,
    Guid WorkerId,
    Guid PositionId,
    string Symbol,
    decimal Quantity,
    decimal EntryPrice,
    DateTimeOffset OpenedAtUtc,
    decimal? StopLossPrice,
    decimal? TakeProfitPrice,
    ExperimentWorker? Worker = null);

public interface IExperimentProtectiveExitPositionSource
{
    Task<IReadOnlyList<ExperimentProtectiveExitPosition>> ListOpenAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed record ExperimentProtectiveExitKey(
    Guid UserId,
    Guid WorkerId,
    Guid PositionId,
    string ProtectiveExitIdentity,
    DateTimeOffset CandleCloseTimeUtc);

public enum ExperimentProtectiveExitClaimResult { Claimed = 0, Existing, Conflict }

/// <summary>
/// Durable implementations atomically claim this identity before any decision or paper command.
/// Existing, rejected, and unknown outcomes are terminal and deliberately never replayed.
/// </summary>
public interface IExperimentProtectiveExitLedger
{
    Task<ExperimentProtectiveExitClaimResult> ClaimAsync(
        ExperimentProtectiveExitKey key,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        ExperimentProtectiveExitKey key,
        ExperimentPaperExecutionStatus status,
        string detail,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryExperimentProtectiveExitLedger : IExperimentProtectiveExitLedger
{
    private readonly ConcurrentDictionary<ExperimentProtectiveExitKey, (ExperimentPaperExecutionStatus Status, string Detail)> _records = new();

    public Task<ExperimentProtectiveExitClaimResult> ClaimAsync(ExperimentProtectiveExitKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_records.TryAdd(key, (ExperimentPaperExecutionStatus.Claimed, string.Empty))
            ? ExperimentProtectiveExitClaimResult.Claimed
            : ExperimentProtectiveExitClaimResult.Existing);
    }

    public Task CompleteAsync(ExperimentProtectiveExitKey key, ExperimentPaperExecutionStatus status, string detail, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_records.ContainsKey(key))
            throw new InvalidOperationException("Only a claimed protective exit may be completed.");
        _records[key] = (status, detail);
        return Task.CompletedTask;
    }
}

public sealed record ExperimentProtectiveExitEvaluationResult(
    ExperimentProtectiveExitKey? Key,
    bool Submitted,
    string Reason);

public interface IExperimentProtectiveExitOwnerEvaluator
{
    Task<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>> EvaluateOwnerAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

public sealed record ExperimentProfitProtectionDecision(
    bool ShouldExit,
    decimal? ExitPrice,
    Candle? TriggerCandle,
    string Reason);

/// <summary>
/// Closed-candle profit protection. It does not predict a top: it requires an already-profitable
/// position, a confirmed 5-minute peak rollover, weakening RSI and MACD, and deterioration on at
/// least one higher timeframe.
/// </summary>
public static class ExperimentProfitProtectionPolicy
{
    public const decimal MinimumRewardRiskMultiple = 0.5m;
    private const int RequiredCandles = 35;

    public static ExperimentProfitProtectionDecision Evaluate(
        ExperimentProtectiveExitPosition position,
        IReadOnlyDictionary<CandleInterval, ExperimentCandleSeries> evidence)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(evidence);

        if (position.StopLossPrice is not decimal stop || stop >= position.EntryPrice)
            return NoExit("A valid opening risk distance is required for profit protection.");
        if (PaperTrainingAutoSelectionService.ApprovedIntervals.Any(interval =>
                !evidence.TryGetValue(interval, out var series)
                || !IsValidSeries(series, position, interval)))
            return NoExit("Complete safe 5m, 15m, 30m, and 1h evidence is required for profit protection.");

        var primary = evidence[CandleInterval.FiveMinutes].Candles;
        var latest = primary[^1];
        decimal minimumProtectedPrice;
        try
        {
            minimumProtectedPrice = checked(position.EntryPrice
                + ((position.EntryPrice - stop) * MinimumRewardRiskMultiple));
        }
        catch (OverflowException)
        {
            return NoExit("Profit-protection threshold arithmetic overflowed.");
        }
        if (latest.Close < minimumProtectedPrice)
            return NoExit("The position has not reached the minimum 0.5R profit-protection threshold.");

        var prior = primary.Take(primary.Count - 1).ToArray();
        var priorRsi = new RelativeStrengthIndexCalculator(14).Calculate(prior);
        var currentRsi = new RelativeStrengthIndexCalculator(14).Calculate(primary);
        var priorMacd = new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(prior);
        var currentMacd = new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(primary);
        if (priorRsi.Value is null || currentRsi.Value is null || priorMacd.Value is null || currentMacd.Value is null)
            return NoExit("Profit-protection indicators do not have sufficient closed history.");

        var confirmedPeak = primary[^2].High > primary[^3].High
            && latest.High < primary[^2].High
            && latest.Close < primary[^2].Close;
        var rsiRolledOver = priorRsi.Value.Value >= 60m
            && currentRsi.Value.Value < priorRsi.Value.Value;
        var macdWeakening = currentMacd.Value.Value.Histogram < priorMacd.Value.Value.Histogram;
        if (!confirmedPeak || !rsiRolledOver || !macdWeakening)
            return NoExit("The profitable 5-minute position has no confirmed RSI/MACD peak rollover.");

        var higherTimeframeConfirmation = PaperTrainingAutoSelectionService.ApprovedIntervals
            .Where(interval => interval != CandleInterval.FiveMinutes)
            .Any(interval => IsWeakening(evidence[interval].Candles));
        if (!higherTimeframeConfirmation)
            return NoExit("No 15m, 30m, or 1h timeframe confirms momentum deterioration.");

        return new(
            true,
            latest.Close,
            latest,
            "Profitable 5-minute peak rollover confirmed by weakening RSI, MACD, and a higher timeframe.");
    }

    private static bool IsValidSeries(
        ExperimentCandleSeries series,
        ExperimentProtectiveExitPosition position,
        CandleInterval interval) =>
        series is not null
        && series.Interval == interval
        && string.Equals(series.Symbol, position.Symbol, StringComparison.OrdinalIgnoreCase)
        && series.Candles.Count >= RequiredCandles
        && series.Candles.All(candle =>
            candle.CanBeUsedForClosedCandleSignal
            && candle.Interval == interval
            && string.Equals(candle.Symbol, position.Symbol, StringComparison.OrdinalIgnoreCase)
            && candle.OpenTimeUtc.Offset == TimeSpan.Zero
            && candle.CloseTimeUtc.Offset == TimeSpan.Zero
            && candle.CloseTimeUtc <= series.AsOfUtc)
        && series.AsOfUtc.Offset == TimeSpan.Zero
        && series.Candles.Zip(series.Candles.Skip(1), static (left, right) =>
            left.CloseTimeUtc == right.OpenTimeUtc).All(value => value)
        && series.Candles[^1].CloseTimeUtc > position.OpenedAtUtc;

    private static bool IsWeakening(IReadOnlyList<Candle> candles)
    {
        var prior = candles.Take(candles.Count - 1).ToArray();
        var priorRsi = new RelativeStrengthIndexCalculator(14).Calculate(prior);
        var currentRsi = new RelativeStrengthIndexCalculator(14).Calculate(candles);
        return priorRsi.Value is not null
            && currentRsi.Value is not null
            && candles[^1].Close < candles[^2].Close
            && currentRsi.Value.Value < priorRsi.Value.Value;
    }

    private static ExperimentProfitProtectionDecision NoExit(string reason) =>
        new(false, null, null, reason);
}

/// <summary>
/// Evaluates only chronological, durable, closed experiment candles and submits a triggered
/// exit through the existing decision ledger and mandatory paper pipeline. It never changes a
/// worker or a position directly.
/// </summary>
public sealed class ExperimentProtectiveExitOrchestrator : IExperimentProtectiveExitOwnerEvaluator
{
    private const int MaximumCandles = DurableExperimentCandleSeriesSource.MaximumRequestedCount;
    private const int ProtectiveExitDecisionVersion = 1;
    private const string ProtectiveExitStrategy = "experiment-protective-exit";
    private const string ProtectiveExitFingerprint = "C4A8BAF784E369F2B28350D871BE6FAE21E9A5D1D2B50D26D7E4432971C68D35";

    private readonly IExperimentProtectiveExitPositionSource _positions;
    private readonly IExperimentCandleSeriesSource _candles;
    private readonly IExperimentProtectiveExitLedger _exits;
    private readonly IExperimentDecisionLedger _decisions;
    private readonly PaperExperimentTradeOrchestrator _paper;
    private readonly TimeProvider _timeProvider;

    public ExperimentProtectiveExitOrchestrator(
        IExperimentProtectiveExitPositionSource positions,
        IExperimentCandleSeriesSource candles,
        IExperimentProtectiveExitLedger exits,
        IExperimentDecisionLedger decisions,
        PaperExperimentTradeOrchestrator paper,
        TimeProvider timeProvider)
    {
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _exits = exits ?? throw new ArgumentNullException(nameof(exits));
        _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>> EvaluateOwnerAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("Owner is required.", nameof(userId));

        var positions = await _positions.ListOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        var results = new List<ExperimentProtectiveExitEvaluationResult>(positions.Count);
        foreach (var position in positions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position.UserId != userId)
            {
                results.Add(new(null, false, "Position source returned foreign owner evidence."));
                continue;
            }

            try
            {
                results.AddRange(await EvaluatePositionAsync(position, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // A malformed paper position must not stop another isolated worker.
            catch (Exception)
#pragma warning restore CA1031
            {
                // An unreadable or malformed position must not block another worker. The worker
                // host logs this safe, non-sensitive reason without exposing exception details.
                results.Add(new(null, false, "Position evaluation faulted safely."));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<ExperimentProtectiveExitEvaluationResult>> EvaluatePositionAsync(
        ExperimentProtectiveExitPosition position,
        CancellationToken cancellationToken)
    {
        if (!IsValid(position))
            return [new(null, false, "Position protective-exit evidence is invalid.")];

        var asOfUtc = _timeProvider.GetUtcNow();
        if (asOfUtc.Offset != TimeSpan.Zero)
            return [new(null, false, "A UTC evaluation clock is required.")];

        var seriesResult = await _candles.GetClosedSeriesAsync(
            new ExperimentCandleSeriesRequest(position.Symbol, CandleInterval.OneMinute, asOfUtc, MaximumCandles),
            cancellationToken).ConfigureAwait(false);
        if (!seriesResult.IsAvailable || seriesResult.Series is null)
            return [new(null, false, $"Closed candle evidence is unavailable: {seriesResult.BlockReason}.")];

        var results = new List<ExperimentProtectiveExitEvaluationResult>();
        foreach (var candle in seriesResult.Series.Candles.OrderBy(candidate => candidate.OpenTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candle.CanBeUsedForClosedCandleSignal || candle.CloseTimeUtc <= position.OpenedAtUtc)
                continue;

            var trigger = EvaluateTrigger(position, candle);
            if (trigger is null)
                continue;

            var triggered = trigger.Value;
            var identity = $"{triggered.Kind}|{position.StopLossPrice}|{position.TakeProfitPrice}";
            results.Add(await SubmitAsync(
                position, candle, triggered.Kind, triggered.ExitPrice, identity, asOfUtc, cancellationToken).ConfigureAwait(false));
            return results;
        }

        var momentum = await EvaluateProfitProtectionAsync(position, asOfUtc, cancellationToken).ConfigureAwait(false);
        if (!momentum.ShouldExit || momentum.ExitPrice is null || momentum.TriggerCandle is null)
            return results;
        var momentumIdentity = $"{ExperimentProtectiveExitKind.MomentumReversal}|v1|{momentum.Reason}";
        results.Add(await SubmitAsync(
            position,
            momentum.TriggerCandle,
            ExperimentProtectiveExitKind.MomentumReversal,
            momentum.ExitPrice.Value,
            momentumIdentity,
            asOfUtc,
            cancellationToken).ConfigureAwait(false));
        return results;
    }

    private async Task<ExperimentProfitProtectionDecision> EvaluateProfitProtectionAsync(
        ExperimentProtectiveExitPosition position,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var reads = PaperTrainingAutoSelectionService.ApprovedIntervals
            .Select(async interval => (
                Interval: interval,
                Result: await _candles.GetClosedSeriesAsync(
                    new ExperimentCandleSeriesRequest(position.Symbol, interval, asOfUtc, 35),
                    cancellationToken).ConfigureAwait(false)))
            .ToArray();
        var completed = await Task.WhenAll(reads).ConfigureAwait(false);
        if (completed.Any(item => !item.Result.IsAvailable || item.Result.Series is null))
            return new(false, null, null, "Complete multi-timeframe profit-protection evidence is unavailable.");
        return ExperimentProfitProtectionPolicy.Evaluate(
            position,
            completed.ToDictionary(item => item.Interval, item => item.Result.Series!));
    }

    private async Task<ExperimentProtectiveExitEvaluationResult> SubmitAsync(
        ExperimentProtectiveExitPosition position,
        Candle candle,
        ExperimentProtectiveExitKind kind,
        decimal exitPrice,
        string identity,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var key = new ExperimentProtectiveExitKey(
            position.UserId, position.WorkerId, position.PositionId, identity, candle.CloseTimeUtc);
        var claim = await _exits.ClaimAsync(key, cancellationToken).ConfigureAwait(false);
        if (claim != ExperimentProtectiveExitClaimResult.Claimed)
            return new(key, false, "Protective exit was already claimed and will not be retried.");

        try
        {
            var decision = CreateDecision(position, candle, kind, identity, asOfUtc);
            var written = await _decisions.RecordAsync(position.UserId, decision, cancellationToken).ConfigureAwait(false);
            if (written.Result == ExperimentDecisionWriteResult.Conflict || written.Record is null)
            {
                await _exits.CompleteAsync(key, ExperimentPaperExecutionStatus.Unknown, "Decision ledger conflict.", cancellationToken).ConfigureAwait(false);
                return new(key, false, "Decision conflict is terminal and requires reconciliation.");
            }

            var worker = position.Worker ?? CreateWorkerView(position);
            var context = new ExperimentPaperWorkerContext(
                worker,
                new ExperimentWorkerPortfolioSnapshot(position.UserId, position.WorkerId, position.Quantity, asOfUtc),
                new ExperimentPaperCandleSnapshot(position.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                    asOfUtc, exitPrice, candle.Volume, candle.QualityFlags.Select(flag => flag.ToString()).ToArray()));
            var result = await _paper.ProcessAsync(written.Record, context, cancellationToken).ConfigureAwait(false);
            var status = result.PipelineResult?.RequiresReconciliation == true ? ExperimentPaperExecutionStatus.Unknown
                : result.PipelineResult?.Executed == true ? ExperimentPaperExecutionStatus.Completed
                : ExperimentPaperExecutionStatus.Blocked;
            await _exits.CompleteAsync(
                key,
                status,
                result.Reason ?? result.PipelineResult?.BlockedReason ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            return new(
                key,
                result.Submitted && result.PipelineResult?.Executed == true,
                result.PipelineResult?.BlockedReason
                ?? (result.Submitted ? "Submitted to the paper pipeline." : result.Reason ?? "Paper pipeline did not supply a reason."));
        }
        catch
        {
            await _exits.CompleteAsync(key, ExperimentPaperExecutionStatus.Unknown, "Pipeline outcome is unknown.", cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsValid(ExperimentProtectiveExitPosition position) =>
        position.UserId != Guid.Empty && position.WorkerId != Guid.Empty && position.PositionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(position.Symbol) && position.Quantity > 0m && position.EntryPrice > 0m &&
        position.OpenedAtUtc != default && position.OpenedAtUtc.Offset == TimeSpan.Zero &&
        (position.StopLossPrice is > 0m || position.TakeProfitPrice is > 0m);

    private static (ExperimentProtectiveExitKind Kind, decimal ExitPrice)? EvaluateTrigger(ExperimentProtectiveExitPosition position, Candle candle)
    {
        var stopTouched = position.StopLossPrice is decimal stop && candle.Low <= stop;
        var targetTouched = position.TakeProfitPrice is decimal target && candle.High >= target;
        if (!stopTouched && !targetTouched)
            return null;

        // OHLC cannot identify the order when both levels occur. Preserve the existing
        // conservative rule: settle as the stop, and honour a gap at the candle open.
        if (stopTouched)
            return (ExperimentProtectiveExitKind.StopLoss, Math.Min(position.StopLossPrice!.Value, candle.Open));
        return (ExperimentProtectiveExitKind.TakeProfit, Math.Max(position.TakeProfitPrice!.Value, candle.Open));
    }

    private static ExperimentDecisionRecord CreateDecision(
        ExperimentProtectiveExitPosition position,
        Candle candle,
        ExperimentProtectiveExitKind kind,
        string triggerIdentity,
        DateTimeOffset asOfUtc)
    {
        var positionFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ProtectiveExitFingerprint}|{position.PositionId:D}|{position.StopLossPrice}|{position.TakeProfitPrice}|{triggerIdentity}")));
        var key = new ExperimentDecisionKey(position.UserId, position.WorkerId, ProtectiveExitDecisionVersion,
            ExperimentResearchGroup.A, ProtectiveExitStrategy, ProtectiveExitDecisionVersion, positionFingerprint,
            position.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc, asOfUtc);
        return new ExperimentDecisionRecord(key, new ExperimentProposal(ExperimentProposalAction.Close,
            $"Protective {kind} triggered from a closed durable candle."), positionFingerprint, asOfUtc);
    }

    private static ExperimentWorker CreateWorkerView(ExperimentProtectiveExitPosition position)
    {
        var worker = new ExperimentWorker(position.WorkerId, position.UserId, "protective-exit", ProtectiveExitStrategy,
            position.Symbol, decimal.MaxValue / 100m, position.OpenedAtUtc, 0);
        worker.Start();
        return worker;
    }
}
