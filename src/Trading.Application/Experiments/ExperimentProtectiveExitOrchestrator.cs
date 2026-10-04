using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public enum ExperimentProtectiveExitKind
{
    StopLoss = 0,
    TakeProfit,
    MomentumReversal,
    StrategyInvalidation,
    MaximumHolding
}

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
    ExperimentWorker? Worker = null,
    CandleInterval SignalInterval = CandleInterval.None,
    int StrategyVersion = 2,
    DateTimeOffset? OpeningSignalCloseUtc = null);

public static class ApprovedStrategyHoldingPolicy
{
    public static bool TryMaximumSignalCandles(
        string strategyId, string? parametersJson, out int candles, out string error)
    {
        if (!ApprovedStrategyParameters.TryNormalize(strategyId, parametersJson, out var normalized, out error))
        {
            candles = 0;
            return false;
        }

        candles = ApprovedStrategyParameters.Read(strategyId, normalized).Int32("maximumHoldingCandles");
        return true;
    }

    public static int MaximumSignalCandles(string strategyId, string? parametersJson = null) =>
        TryMaximumSignalCandles(strategyId, parametersJson, out var candles, out var error)
            ? candles
            : throw new InvalidOperationException($"Saved maximum-holding settings require review: {error}");

    public static bool IsExpired(ExperimentProtectiveExitPosition position, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(position);
        var candles = MaximumSignalCandles(
            position.Worker?.StrategyId ?? string.Empty,
            position.Worker?.StrategyParameters);
        if (candles == 0 || position.SignalInterval is CandleInterval.None or CandleInterval.FourDays)
            return false;
        var duration = TimeSpan.FromMinutes(checked((int)position.SignalInterval * candles));
        return asOfUtc >= position.OpenedAtUtc.Add(duration);
    }
}

public sealed record ApprovedStrategyInvalidation(bool ShouldExit, string Reason, bool IsBlocked = false);

public static class ApprovedStrategyInvalidationPolicy
{
    public static bool RequiresVersionedEvidence(string strategyId, int strategyVersion) =>
        strategyVersion >= 5 && strategyId == "platform.regime-switching-ensemble"
        || strategyVersion >= 4 && strategyId is
            "platform.ema-trend-continuation" or "platform.donchian-breakout-ensemble"
            or "platform.bollinger-mean-reversion" or "platform.rsi-pullback"
            or "platform.macd-volume" or "platform.volatility-compression-breakout"
            or "platform.cross-sectional-momentum-rotation"
            or "platform.three-swing-channel-divergence";

    public static ApprovedStrategyInvalidation Evaluate(
        string strategyId,
        IReadOnlyList<Candle> signalCandles,
        int strategyVersion = 2,
        string? strategyParameters = null,
        DateTimeOffset? openingSignalCloseUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentNullException.ThrowIfNull(signalCandles);
        StrategyParameterValues? settings = null;
        if (RequiresVersionedEvidence(strategyId, strategyVersion))
        {
            if (string.IsNullOrWhiteSpace(strategyParameters))
                return new(false, "Saved strategy invalidation settings require review: missing position parameters.", true);
            if (!ApprovedStrategyParameters.TryNormalize(strategyId, strategyParameters,
                    out var normalized, out var error))
                return new(false, $"Saved strategy invalidation settings require review: {error}", true);
            settings = ApprovedStrategyParameters.Read(strategyId, normalized);
        }
        var minimumHistory = strategyId switch
        {
            "platform.ema-trend-continuation" => settings?.Int32("signalInvalidationEma") ?? 50,
            "platform.donchian-breakout-ensemble" => settings?.Int32("exitChannel") + 1 ?? 21,
            "platform.bollinger-mean-reversion" => settings?.Int32("bollingerPeriod") ?? 20,
            "platform.rsi-pullback" => Math.Max(settings?.Int32("rsiPeriod") + 1 ?? 15,
                settings?.Int32("signalEma") ?? 20),
            "platform.macd-volume" => Math.Max(
                (settings?.Int32("macdSlow") ?? 26) + (settings?.Int32("macdSignal") ?? 9) + 1,
                settings?.Int32("signalEma") ?? 50),
            "platform.volatility-compression-breakout" => settings?.Int32("exitChannel") + 1 ?? 21,
            "platform.cross-sectional-momentum-rotation" => settings?.Int32("trendEma") ?? 200,
            "platform.three-swing-channel-divergence" => Math.Max(
                settings?.Int32("channelPeriod") ?? 50,
                (settings?.Int32("macdSlow") ?? 26) + (settings?.Int32("macdSignal") ?? 9) + 1),
            "platform.regime-switching-ensemble" => settings?.Int32("stopSwingLookback") + 1 ?? 51,
            _ => 51
        };
        if (signalCandles.Count < Math.Max(51, minimumHistory))
            return new(false, $"Strategy invalidation requires at least {Math.Max(51, minimumHistory)} closed signal candles.",
                settings is not null);

        var current = signalCandles[^1];
        if (settings is not null)
        {
            if (openingSignalCloseUtc is not DateTimeOffset openingClose
                || openingClose.Offset != TimeSpan.Zero)
                return new(false, "Strategy invalidation requires an attested UTC opening signal close.", true);
            if (current.CloseTimeUtc <= openingClose)
                return new(false, "A later closed signal candle is required to invalidate the opening decision.");
            if (!signalCandles.Any(candle => candle.CloseTimeUtc == openingClose))
                return new(false, "Strategy invalidation requires the original opening candle in closed history.", true);
        }
        var prior = signalCandles.Take(signalCandles.Count - 1).ToArray();
        bool invalidated;
        string reason;
        switch (strategyId)
        {
                case "platform.ema-trend-continuation":
                    var invalidationEma = settings?.Int32("signalInvalidationEma") ?? 50;
                    if (strategyVersion >= 5)
                    {
                        if (settings?.String("emaPlanModel") != "pullbackSwing")
                            return new(false, "Saved EMA protection settings require owner review.", true);
                        var openingIndex = Array.FindIndex(signalCandles.ToArray(),
                            candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                        var swingLookback = settings.Int32("stopSwingLookback");
                        if (openingIndex < swingLookback - 1)
                            return new(false, "EMA pullback invalidation requires the full opening swing.", true);
                        var swingLow = signalCandles.Skip(openingIndex - swingLookback + 1)
                            .Take(swingLookback).Min(candle => candle.Low);
                        invalidated = current.Close < swingLow
                            || current.Close < Ema(signalCandles, invalidationEma);
                        reason = $"Signal close broke the frozen EMA pullback swing low {swingLow} or EMA {invalidationEma}.";
                        break;
                    }
                    invalidated = current.Close < Ema(signalCandles, invalidationEma);
                    reason = $"Signal close crossed below EMA {invalidationEma}.";
                    break;
                case "platform.donchian-breakout-ensemble":
                case "platform.session-conditioned-breakout":
                    var exitChannel = settings?.Int32("exitChannel") ?? 20;
                    invalidated = current.Close < prior.TakeLast(exitChannel).Min(candle => candle.Low);
                    reason = $"Signal close broke the prior Donchian {exitChannel} exit channel.";
                    break;
                case "platform.bollinger-mean-reversion":
                    var bandPeriod = settings?.Int32("bollingerPeriod") ?? 20;
                    if (strategyVersion >= 5)
                    {
                        if (settings?.String("bollingerPlanModel") != "excursionMidBand")
                            return new(false, "Saved Bollinger protection settings require owner review.", true);
                        var openingIndex = Array.FindIndex(signalCandles.ToArray(),
                            candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                        if (openingIndex < 1)
                            return new(false, "Bollinger excursion invalidation requires the closed pre-entry excursion.", true);
                        var excursionLow = Math.Min(signalCandles[openingIndex - 1].Low,
                            signalCandles[openingIndex].Low);
                        invalidated = current.Close < excursionLow;
                        reason = $"Signal close broke the frozen Bollinger excursion/re-entry low {excursionLow}.";
                    }
                    else
                    {
                        invalidated = current.Close >= Bands(signalCandles, bandPeriod).Middle;
                        reason = $"Range-reversion price reached the Bollinger {bandPeriod} middle-band target.";
                    }
                    break;
                case "platform.rsi-pullback":
                    var rsiPeriod = settings?.Int32("rsiPeriod") ?? 14;
                    var signalEma = settings?.Int32("signalEma") ?? 20;
                    var exitRsi = settings?.Decimal("longExitRsi") ?? 70m;
                    if (strategyVersion >= 5)
                    {
                        if (settings?.String("rsiPlanModel") != "pullbackSwing")
                            return new(false, "Saved RSI pullback protection settings require owner review.", true);
                        var openingIndex = Array.FindIndex(signalCandles.ToArray(),
                            candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                        var swingLookback = settings.Int32("stopSwingLookback");
                        if (openingIndex < swingLookback - 1)
                            return new(false, "RSI pullback invalidation requires the full pre-entry swing.", true);
                        var swingLow = signalCandles.Skip(openingIndex - swingLookback + 1)
                            .Take(swingLookback).Min(candle => candle.Low);
                        invalidated = current.Close < swingLow
                            || Rsi(signalCandles, rsiPeriod) >= exitRsi
                            || current.Close < Ema(signalCandles, signalEma);
                        reason = $"Signal close broke the frozen RSI pullback swing low {swingLow}, RSI {rsiPeriod} reached {exitRsi}, or EMA {signalEma} failed.";
                        break;
                    }
                    invalidated = Rsi(signalCandles, rsiPeriod) >= exitRsi
                        || current.Close < Ema(signalCandles, signalEma);
                    reason = $"RSI {rsiPeriod} upper target {exitRsi} or signal EMA {signalEma} invalidation was reached.";
                    break;
                case "platform.macd-volume":
                    var macdFast = settings?.Int32("macdFast") ?? 12;
                    var macdSlow = settings?.Int32("macdSlow") ?? 26;
                    var macdSignal = settings?.Int32("macdSignal") ?? 9;
                    var trendEma = settings?.Int32("signalEma") ?? 50;
                    var macd = Macd(signalCandles, macdFast, macdSlow, macdSignal);
                    var priorMacd = Macd(prior, macdFast, macdSlow, macdSignal);
                    if (strategyVersion >= 5)
                    {
                        if (settings?.String("macdPlanModel") != "crossSwing")
                            return new(false, "Saved MACD protection settings require owner review.", true);
                        var openingIndex = Array.FindIndex(signalCandles.ToArray(),
                            candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                        var swingLookback = settings.Int32("stopSwingLookback");
                        if (openingIndex < swingLookback - 1)
                            return new(false, "MACD cross invalidation requires the full opening swing.", true);
                        var swingLow = signalCandles.Skip(openingIndex - swingLookback + 1)
                            .Take(swingLookback).Min(candle => candle.Low);
                        invalidated = current.Close < swingLow
                            || macd.Line < macd.Signal
                            || macd.Histogram < priorMacd.Histogram
                            || current.Close < Ema(signalCandles, trendEma);
                        reason = $"Signal close broke the frozen MACD opening swing low {swingLow}, MACD {macdFast}/{macdSlow}/{macdSignal} reversed or slowed, or EMA {trendEma} failed.";
                        break;
                    }
                    invalidated = macd.Line < macd.Signal
                        || macd.Histogram < priorMacd.Histogram
                        || current.Close < Ema(signalCandles, trendEma);
                    reason = $"MACD {macdFast}/{macdSlow}/{macdSignal} reverse cross, histogram deterioration, or EMA {trendEma} failure occurred.";
                    break;
                case "platform.volatility-compression-breakout":
                    if (strategyVersion >= 5 && settings?.String("compressionPlanModel") != "priorRange")
                        return new(false, "Saved compression protection settings require owner review.", true);
                    var compressionChannel = settings?.Int32("exitChannel") ?? 20;
                    if (settings is not null)
                    {
                        var openingIndex = Array.FindIndex(signalCandles.ToArray(),
                            candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                        if (openingIndex < compressionChannel)
                            return new(false, "Frozen compression boundary requires complete pre-break candles.", true);
                        var frozenHigh = signalCandles.Skip(openingIndex - compressionChannel)
                            .Take(compressionChannel).Max(candle => candle.High);
                        invalidated = current.Close <= frozenHigh;
                    }
                    else
                        invalidated = current.Close <= prior.TakeLast(compressionChannel)
                            .Max(candle => candle.High);
                    reason = $"Price closed back inside the {compressionChannel}-candle compression boundary.";
                    break;
                case "platform.cross-sectional-momentum-rotation":
                    if (strategyVersion < 4)
                        return new(false, "This family uses consensus, rank, or portfolio invalidation.");
                    if (settings?.String("momentumPlanModel") != "dailySwing")
                        return new(false, "Saved momentum protection settings require owner review.", true);
                    var dailyOpeningIndex = Array.FindIndex(signalCandles.ToArray(),
                        candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                    var dailySwingLookback = settings.Int32("stopSwingLookback");
                    if (dailyOpeningIndex < dailySwingLookback)
                        return new(false, "Momentum invalidation requires the full closed pre-entry daily swing.", true);
                    var dailySwingLow = signalCandles.Skip(dailyOpeningIndex - dailySwingLookback)
                        .Take(dailySwingLookback).Min(candle => candle.Low);
                    var dailyTrendEma = settings.Int32("trendEma");
                    invalidated = current.Close < dailySwingLow
                        || current.Close < Ema(signalCandles, dailyTrendEma);
                    reason = $"Daily close broke the frozen momentum swing low {dailySwingLow} or EMA {dailyTrendEma}.";
                    break;
                case "platform.three-swing-channel-divergence":
                    if (strategyVersion < 4)
                        return new(false, "Previous three-swing positions use their recorded numeric protection.");
                    if (settings?.String("threeSwingPlanModel") != "confirmedPivot")
                        return new(false, "Saved three-swing protection settings require owner review.", true);
                    var swingOpeningIndex = Array.FindIndex(signalCandles.ToArray(),
                        candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                    var swingChannel = settings.Int32("channelPeriod");
                    if (swingOpeningIndex < swingChannel - 1)
                        return new(false, "Three-swing invalidation requires the complete frozen opening channel.", true);
                    var frozenChannelLow = signalCandles.Skip(swingOpeningIndex - swingChannel + 1)
                        .Take(swingChannel).Min(candle => candle.Low);
                    var swingMacd = Macd(signalCandles,
                        settings.Int32("macdFast"), settings.Int32("macdSlow"),
                        settings.Int32("macdSignal"));
                    invalidated = current.Close < frozenChannelLow
                        || settings.Boolean("exitOnMacdReversal") && swingMacd.Line < swingMacd.Signal;
                    reason = $"Signal close broke the frozen third-swing channel low {frozenChannelLow} or configured MACD reversed.";
                    break;
                case "platform.regime-switching-ensemble":
                    if (strategyVersion < 5)
                        return new(false, "Previous ensemble positions use their recorded numeric protection.");
                    if (settings?.String("regimePlanModel") != "fourHourSwing")
                        return new(false, "Saved ensemble protection settings require owner review.", true);
                    var regimeOpeningIndex = Array.FindIndex(signalCandles.ToArray(),
                        candle => candle.CloseTimeUtc == openingSignalCloseUtc);
                    var regimeSwingLookback = settings.Int32("stopSwingLookback");
                    if (regimeOpeningIndex < regimeSwingLookback)
                        return new(false, "Ensemble invalidation requires the complete pre-entry four-hour swing.", true);
                    var regimeSwingLow = signalCandles.Skip(regimeOpeningIndex - regimeSwingLookback)
                        .Take(regimeSwingLookback).Min(candle => candle.Low);
                    invalidated = current.Close < regimeSwingLow;
                    reason = $"Signal close broke the frozen ensemble four-hour swing low {regimeSwingLow}.";
                    break;
                default:
                    return new(false, "This family uses consensus, rank, or portfolio invalidation.");
        }
        return new(invalidated, invalidated ? reason : "No family-specific invalidation was observed.");
    }

    private static decimal Ema(IReadOnlyList<Candle> candles, int period) =>
        new ExponentialMovingAverageCalculator(period).Calculate(candles).Value!.Value;

    private static decimal Rsi(IReadOnlyList<Candle> candles, int period) =>
        new RelativeStrengthIndexCalculator(period).Calculate(candles).Value!.Value;

    private static BollingerBandsValue Bands(IReadOnlyList<Candle> candles, int period) =>
        new BollingerBandsCalculator(period, 2m).Calculate(candles).Value!.Value;

    private static MacdValue Macd(IReadOnlyList<Candle> candles, int fast, int slow, int signal) =>
        new MovingAverageConvergenceDivergenceCalculator(fast, slow, signal).Calculate(candles).Value!.Value;
}

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

    // Null for a separate trigger ledger; a shared paper ledger supplies its already-claimed association.
    ExperimentPaperExecutionAssociation? GetPaperExecutionClaim(ExperimentProtectiveExitKey key);

    Task CompleteAsync(
        ExperimentProtectiveExitKey key,
        ExperimentPaperExecutionStatus status,
        string detail,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryExperimentProtectiveExitLedger : IExperimentProtectiveExitLedger
{
    private readonly ConcurrentDictionary<ExperimentProtectiveExitKey, (ExperimentPaperExecutionStatus Status, string Detail)> _records = new();

    public ExperimentPaperExecutionAssociation? GetPaperExecutionClaim(ExperimentProtectiveExitKey key) => null;

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
        if (status is not (ExperimentPaperExecutionStatus.Completed
            or ExperimentPaperExecutionStatus.Blocked or ExperimentPaperExecutionStatus.Unknown))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (!_records.TryGetValue(key, out var current)
            || current.Status != ExperimentPaperExecutionStatus.Claimed
            || !_records.TryUpdate(key, (status, detail), current))
            throw new InvalidOperationException("Only an unresolved Claimed protective exit may record its first outcome.");
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
    internal static CandleInterval[] ConfirmationIntervals { get; } =
    [
        CandleInterval.FiveMinutes,
        CandleInterval.FifteenMinutes,
        CandleInterval.ThirtyMinutes,
        CandleInterval.OneHour
    ];

    public static ExperimentProfitProtectionDecision Evaluate(
        ExperimentProtectiveExitPosition position,
        IReadOnlyDictionary<CandleInterval, ExperimentCandleSeries> evidence)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(evidence);

        if (position.StopLossPrice is not decimal stop || stop >= position.EntryPrice)
            return NoExit("A valid opening risk distance is required for profit protection.");
        if (ConfirmationIntervals.Any(interval =>
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

        var higherTimeframeConfirmation = ConfirmationIntervals
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
            catch (Exception exception)
#pragma warning restore CA1031
            {
                // An unreadable or malformed position must not block another worker. The worker
                // host logs this safe, non-sensitive reason without exposing exception details.
                results.Add(new(null, false, $"Position evaluation faulted safely: {exception.GetType().Name}."));
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

        var closedCandles = seriesResult.Series.Candles.OrderBy(candidate => candidate.OpenTimeUtc).ToArray();
        var latest = closedCandles.LastOrDefault();
        if (latest is null || !latest.CanBeUsedForClosedCandleSignal
            || latest.CloseTimeUtc > asOfUtc || asOfUtc - latest.CloseTimeUtc > TimeSpan.FromMinutes(1))
            return [new(null, false, "A fresh closed one-minute paper exit reference is unavailable.")];

        var results = new List<ExperimentProtectiveExitEvaluationResult>();
        foreach (var candle in closedCandles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candle.CanBeUsedForClosedCandleSignal || candle.CloseTimeUtc <= position.OpenedAtUtc)
                continue;

            var trigger = EvaluateTrigger(position, candle);
            if (trigger is null)
                continue;

            var triggered = trigger.Value;
            var identity = $"{triggered}|{position.StopLossPrice}|{position.TakeProfitPrice}";
            results.Add(await SubmitAsync(
                position, candle, latest, triggered, identity, asOfUtc, cancellationToken).ConfigureAwait(false));
            return results;
        }

        var invalidation = await EvaluateStrategyInvalidationAsync(
            position,
            asOfUtc,
            cancellationToken).ConfigureAwait(false);
        if (invalidation.Decision.IsBlocked)
            results.Add(new(null, false, invalidation.Decision.Reason));
        if (invalidation.Decision.ShouldExit && invalidation.Candle is not null)
        {
            var identity = $"{ExperimentProtectiveExitKind.StrategyInvalidation}|v2|{invalidation.Decision.Reason}";
            results.Add(await SubmitAsync(
                position,
                invalidation.Candle,
                latest,
                ExperimentProtectiveExitKind.StrategyInvalidation,
                identity,
                asOfUtc,
                cancellationToken).ConfigureAwait(false));
            return results;
        }

        var momentum = await EvaluateProfitProtectionAsync(position, asOfUtc, cancellationToken).ConfigureAwait(false);
        if (momentum.ShouldExit && momentum.ExitPrice is not null && momentum.TriggerCandle is not null)
        {
            var momentumIdentity = $"{ExperimentProtectiveExitKind.MomentumReversal}|v1|{momentum.Reason}";
            results.Add(await SubmitAsync(
                position,
                momentum.TriggerCandle,
                latest,
                ExperimentProtectiveExitKind.MomentumReversal,
                momentumIdentity,
                asOfUtc,
                cancellationToken).ConfigureAwait(false));
            return results;
        }

        if (!ApprovedStrategyHoldingPolicy.TryMaximumSignalCandles(
                position.Worker?.StrategyId ?? string.Empty,
                position.Worker?.StrategyParameters,
                out _, out var holdingError))
            results.Add(new(null, false, $"Saved maximum-holding settings require review: {holdingError}"));
        else if (ApprovedStrategyHoldingPolicy.IsExpired(position, latest.CloseTimeUtc))
        {
            var identity = $"{ExperimentProtectiveExitKind.MaximumHolding}|v2|{position.SignalInterval}";
            results.Add(await SubmitAsync(
                position,
                latest,
                latest,
                ExperimentProtectiveExitKind.MaximumHolding,
                identity,
                asOfUtc,
                cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<(ApprovedStrategyInvalidation Decision, Candle? Candle)> EvaluateStrategyInvalidationAsync(
        ExperimentProtectiveExitPosition position,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        if (position.Worker is null
            || position.SignalInterval is CandleInterval.None or CandleInterval.FourDays)
        {
            return (new(false, "Strategy identity and signal interval are required."), null);
        }
        var duration = TimeSpan.FromMinutes((int)position.SignalInterval);
        var aligned = new DateTimeOffset(
            asOfUtc.UtcTicks - asOfUtc.UtcTicks % duration.Ticks,
            TimeSpan.Zero);
        var result = await _candles.GetClosedSeriesAsync(
            new ExperimentCandleSeriesRequest(
                position.Symbol,
                position.SignalInterval,
                aligned,
                ApprovedConsensusStrategyProfiles.RequiredHistory),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsAvailable || result.Series is null)
            return (new(false, "Complete strategy invalidation evidence is unavailable.",
                ApprovedStrategyInvalidationPolicy.RequiresVersionedEvidence(
                    position.Worker.StrategyId, position.StrategyVersion)), null);
        return (
            ApprovedStrategyInvalidationPolicy.Evaluate(
                position.Worker.StrategyId,
                result.Series.Candles,
                position.StrategyVersion,
                position.Worker.StrategyParameters,
                position.OpeningSignalCloseUtc),
            result.Series.Candles[^1]);
    }

    private async Task<ExperimentProfitProtectionDecision> EvaluateProfitProtectionAsync(
        ExperimentProtectiveExitPosition position,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var reads = ExperimentProfitProtectionPolicy.ConfirmationIntervals
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
        Candle triggerCandle,
        Candle executionCandle,
        ExperimentProtectiveExitKind kind,
        string identity,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        var key = new ExperimentProtectiveExitKey(
            position.UserId, position.WorkerId, position.PositionId, identity, triggerCandle.CloseTimeUtc);
        if (position.Worker is null || position.Worker.PositionQuantity != position.Quantity)
            return new(key, false, "Protective exit requires the matching durable worker position.");
        var claim = await _exits.ClaimAsync(key, cancellationToken).ConfigureAwait(false);
        if (claim != ExperimentProtectiveExitClaimResult.Claimed)
            return new(key, false, "Protective exit was already claimed and will not be retried.");

        try
        {
            var decision = CreateDecision(position, executionCandle, kind, identity, asOfUtc);
            var written = await _decisions.RecordAsync(position.UserId, decision, cancellationToken).ConfigureAwait(false);
            if (written.Result == ExperimentDecisionWriteResult.Conflict || written.Record is null)
            {
                await _exits.CompleteAsync(key, ExperimentPaperExecutionStatus.Unknown, "Decision ledger conflict.", cancellationToken).ConfigureAwait(false);
                return new(key, false, "Decision conflict is terminal and requires reconciliation.");
            }

            var context = new ExperimentPaperWorkerContext(
                position.Worker,
                new ExperimentWorkerPortfolioSnapshot(position.UserId, position.WorkerId, position.Quantity, asOfUtc),
                new ExperimentPaperCandleSnapshot(position.Symbol, executionCandle.Interval, executionCandle.OpenTimeUtc, executionCandle.CloseTimeUtc,
                    asOfUtc, executionCandle.Close, executionCandle.Volume, executionCandle.QualityFlags.Select(flag => flag.ToString()).ToArray()));
            var paperClaim = _exits.GetPaperExecutionClaim(key);
            var result = paperClaim is null
                ? await _paper.ProcessAsync(written.Record, context, cancellationToken).ConfigureAwait(false)
                : await _paper.ProcessPreclaimedProtectiveExitAsync(
                    written.Record, context, paperClaim, cancellationToken).ConfigureAwait(false);
            var status = result.PipelineResult?.RequiresReconciliation == true ? ExperimentPaperExecutionStatus.Unknown
                : result.PipelineResult?.Executed == true ? ExperimentPaperExecutionStatus.Completed
                : ExperimentPaperExecutionStatus.Blocked;
            if (paperClaim is null || !result.Submitted)
                await _exits.CompleteAsync(
                    key,
                    paperClaim is not null ? ExperimentPaperExecutionStatus.Unknown : status,
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
            // A completed shared claim may already have been persisted by the paper pipeline.
            // Never overwrite its first outcome while handling a later failure.
            if (_exits.GetPaperExecutionClaim(key) is not { } paperClaim
                || await _paper.IsPreclaimedExecutionPendingAsync(paperClaim, cancellationToken).ConfigureAwait(false))
                await _exits.CompleteAsync(key, ExperimentPaperExecutionStatus.Unknown, "Pipeline outcome is unknown.", cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsValid(ExperimentProtectiveExitPosition position) =>
        position.UserId != Guid.Empty && position.WorkerId != Guid.Empty && position.PositionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(position.Symbol) && position.Quantity > 0m && position.EntryPrice > 0m &&
        position.OpenedAtUtc != default && position.OpenedAtUtc.Offset == TimeSpan.Zero &&
        (position.StopLossPrice is > 0m || position.TakeProfitPrice is > 0m);

    private static ExperimentProtectiveExitKind? EvaluateTrigger(ExperimentProtectiveExitPosition position, Candle candle)
    {
        var stopTouched = position.StopLossPrice is decimal stop && candle.Low <= stop;
        var targetTouched = position.TakeProfitPrice is decimal target && candle.High >= target;
        if (!stopTouched && !targetTouched)
            return null;

        // OHLC cannot identify the order when both levels occur. Prefer the stop;
        // the simulated close itself uses the later fresh one-minute reference.
        if (stopTouched)
            return ExperimentProtectiveExitKind.StopLoss;
        return ExperimentProtectiveExitKind.TakeProfit;
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
            $"Protective {kind} triggered from a closed durable candle; the paper reduction uses the latest closed one-minute reference."), positionFingerprint, asOfUtc);
    }

}
