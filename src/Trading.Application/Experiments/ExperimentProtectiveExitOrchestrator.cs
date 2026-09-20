using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public enum ExperimentProtectiveExitKind { StopLoss = 0, TakeProfit }

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
    decimal? TakeProfitPrice);

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
            var key = new ExperimentProtectiveExitKey(position.UserId, position.WorkerId, position.PositionId, identity, candle.CloseTimeUtc);
            var claim = await _exits.ClaimAsync(key, cancellationToken).ConfigureAwait(false);
            if (claim != ExperimentProtectiveExitClaimResult.Claimed)
            {
                results.Add(new(key, false, "Protective exit was already claimed and will not be retried."));
                return results;
            }

            try
            {
                var decision = CreateDecision(position, candle, triggered.Kind, asOfUtc);
                var written = await _decisions.RecordAsync(position.UserId, decision, cancellationToken).ConfigureAwait(false);
                if (written.Result == ExperimentDecisionWriteResult.Conflict || written.Record is null)
                {
                    await _exits.CompleteAsync(key, ExperimentPaperExecutionStatus.Unknown, "Decision ledger conflict.", cancellationToken).ConfigureAwait(false);
                    results.Add(new(key, false, "Decision conflict is terminal and requires reconciliation."));
                    return results;
                }

                var worker = CreateWorkerView(position);
                var context = new ExperimentPaperWorkerContext(
                    worker,
                    new ExperimentWorkerPortfolioSnapshot(position.UserId, position.WorkerId, position.Quantity, asOfUtc),
                    new ExperimentPaperCandleSnapshot(position.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                        asOfUtc, triggered.ExitPrice, candle.Volume, candle.QualityFlags.Select(flag => flag.ToString()).ToArray()));
                var result = await _paper.ProcessAsync(written.Record, context, cancellationToken).ConfigureAwait(false);
                var status = result.PipelineResult?.RequiresReconciliation == true ? ExperimentPaperExecutionStatus.Unknown
                    : result.PipelineResult?.Executed == true ? ExperimentPaperExecutionStatus.Completed
                    : ExperimentPaperExecutionStatus.Blocked;
                await _exits.CompleteAsync(key, status, result.Reason ?? result.PipelineResult?.BlockedReason ?? string.Empty, cancellationToken).ConfigureAwait(false);
                results.Add(new(key, result.Submitted && result.PipelineResult?.Executed == true,
                    result.PipelineResult?.BlockedReason ?? (result.Submitted ? "Submitted to the paper pipeline." : result.Reason ?? "Paper pipeline did not supply a reason.")));
                return results;
            }
            catch
            {
                await _exits.CompleteAsync(key, ExperimentPaperExecutionStatus.Unknown, "Pipeline outcome is unknown.", cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        return results;
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
        DateTimeOffset asOfUtc)
    {
        var positionFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{ProtectiveExitFingerprint}|{position.PositionId:D}|{position.StopLossPrice}|{position.TakeProfitPrice}")));
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
