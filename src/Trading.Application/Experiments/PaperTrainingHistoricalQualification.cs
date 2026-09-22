using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.Application.Experiments;

public sealed record PaperTrainingQualificationRequest(
    IReadOnlyList<PaperTrainingWorkerSlot> Slots,
    CandleInterval Interval,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    PaperTrainingQualificationGate Gate);

/// <summary>
/// Replays the same compiled experiment evaluators used by forward paper training over immutable
/// closed candles. Each accepted observation is modeled as one next-candle round trip, including
/// adverse slippage and taker fees, so qualification never claims exchange execution precision.
/// </summary>
public sealed class PaperTrainingHistoricalQualification
{
    private const decimal TakerFeeRate = 0.0026m;
    private const decimal SlippageRate = 0.001m;
    private readonly IHistoricalCandleSource _candles;
    private readonly ApprovedExperimentStrategyRegistry _strategies;
    private static readonly HashSet<string> s_supportedStrategies = new(StringComparer.Ordinal)
    {
        "platform.ema-trend-continuation",
        "platform.donchian-breakout-ensemble",
        "platform.bollinger-mean-reversion",
        "platform.rsi-pullback",
        "platform.macd-volume",
        "platform.volatility-compression-breakout",
        "platform.rsi-macd-confluence",
        "platform.ema-rsi-trend",
        "platform.bollinger-macd-recovery",
        "platform.donchian-volume-breakout",
        "platform.ema-volume-pullback"
    };

    public static bool Supports(string strategyId) =>
        !string.IsNullOrWhiteSpace(strategyId) && s_supportedStrategies.Contains(strategyId.Trim());

    public PaperTrainingHistoricalQualification(
        IHistoricalCandleSource candles,
        ApprovedExperimentStrategyRegistry strategies)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
    }

    public async Task<IReadOnlyList<PaperTrainingQualificationResult>> RunAsync(
        PaperTrainingQualificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Gate.Validate();
        if (request.Slots.Count is < 1 or > 250)
            throw new ArgumentOutOfRangeException(nameof(request), "Qualification requires one to 250 bounded candidates.");
        if (request.Interval is CandleInterval.None or CandleInterval.FourDays)
            throw new ArgumentOutOfRangeException(nameof(request), "A supported normalized interval is required.");
        if (request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero
            || request.ToUtc <= request.FromUtc || request.ToUtc > DateTimeOffset.UtcNow)
            throw new ArgumentException("Qualification requires a completed UTC historical range.", nameof(request));

        var results = new List<PaperTrainingQualificationResult>(request.Slots.Count);
        foreach (var symbolGroup in request.Slots.GroupBy(slot => slot.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candles = await LoadCandlesAsync(symbolGroup.Key, request, cancellationToken).ConfigureAwait(false);
            foreach (var slot in symbolGroup)
                results.Add(Qualify(slot, request, candles, cancellationToken));
        }
        return results;
    }

    private async Task<Candle[]> LoadCandlesAsync(
        string symbol,
        PaperTrainingQualificationRequest request,
        CancellationToken cancellationToken)
    {
        return (await _candles.FetchAsync(symbol, request.Interval, request.FromUtc, cancellationToken)
                .ConfigureAwait(false))
            .Where(candle => candle.OpenTimeUtc >= request.FromUtc
                && candle.CloseTimeUtc <= request.ToUtc
                && candle.CanBeUsedForClosedCandleSignal)
            .OrderBy(candle => candle.OpenTimeUtc)
            .ToArray();
    }

    private PaperTrainingQualificationResult Qualify(
        PaperTrainingWorkerSlot slot,
        PaperTrainingQualificationRequest request,
        Candle[] candles,
        CancellationToken cancellationToken)
    {
        var fingerprint = Fingerprint(candles);
        if (candles.Length < 30)
            return Rejected(slot, fingerprint, "At least 30 safe closed candles are required.");
        var intervalDuration = TimeSpan.FromMinutes((int)request.Interval);
        if (candles.Any(candle =>
                !candle.Symbol.Equals(slot.Symbol, StringComparison.OrdinalIgnoreCase)
                || candle.Interval != request.Interval
                || candle.CloseTimeUtc - candle.OpenTimeUtc != intervalDuration)
            || candles.Select(candle => candle.OpenTimeUtc).Distinct().Count() != candles.Length
            || candles.Zip(candles.Skip(1), (left, right) => left.CloseTimeUtc == right.OpenTimeUtc).Any(consecutive => !consecutive))
            return Rejected(
                slot,
                fingerprint,
                "Historical candles contain a wrong symbol, interval, duration, gap, duplicate, or out-of-order value.");

        var cash = slot.StartingCash;
        var peak = cash;
        var maximumDrawdown = 0m;
        var completedTrades = 0;
        for (var index = 2; index < candles.Length - 2; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Candle> prefix = new ArraySegment<Candle>(candles, 0, index + 1);
            var series = new ExperimentCandleSeries(
                slot.Symbol, request.Interval, candles[index].CloseTimeUtc, prefix, "kraken-historical-qualification");
            var observation = _strategies.EvaluateHistorical(slot.StrategyId, series, "{}");
            if (observation.Outcome != ExperimentAnalysisOutcome.Analyzed)
                continue;

            // The completed signal candle cannot be filled at its already-observed close. Model
            // entry and exit at later candle opens so no execution price was available to the
            // evaluator when it produced the observation.
            var entry = candles[index + 1].Open * (1m + SlippageRate);
            var exit = candles[index + 2].Open * (1m - SlippageRate);
            if (entry <= 0m || exit <= 0m)
                continue;
            var quantity = cash / (entry * (1m + TakerFeeRate));
            var exitFee = quantity * exit * TakerFeeRate;
            cash = (quantity * exit) - exitFee;
            completedTrades++;
            peak = Math.Max(peak, cash);
            if (peak > 0m)
                maximumDrawdown = Math.Max(maximumDrawdown, ((peak - cash) / peak) * 100m);
            index += 2;
        }

        var netReturn = ((cash - slot.StartingCash) / slot.StartingCash) * 100m;
        var accepted = netReturn >= request.Gate.MinimumNetReturnPercent
            && completedTrades >= request.Gate.MinimumCompletedTrades
            && maximumDrawdown <= request.Gate.MaximumDrawdownPercent;
        var reason = accepted
            ? "Historical qualification passed all configured gates."
            : string.Create(CultureInfo.InvariantCulture,
                $"Historical qualification failed: return {netReturn:F4}% (minimum {request.Gate.MinimumNetReturnPercent:F4}%), " +
                $"completed trades {completedTrades} (minimum {request.Gate.MinimumCompletedTrades}), " +
                $"drawdown {maximumDrawdown:F4}% (maximum {request.Gate.MaximumDrawdownPercent:F4}%).");
        return new(
            slot.Slot,
            slot.Symbol,
            accepted,
            netReturn,
            completedTrades,
            maximumDrawdown,
            fingerprint,
            reason,
            slot.StrategyId,
            Interval: request.Interval);
    }

    private static PaperTrainingQualificationResult Rejected(
        PaperTrainingWorkerSlot slot,
        string fingerprint,
        string reason) =>
        new(slot.Slot, slot.Symbol, false, 0m, 0, 0m, fingerprint, reason, slot.StrategyId,
            Interval: slot.Interval);

    private static string Fingerprint(IReadOnlyList<Candle> candles)
    {
        var canonical = string.Join('\n', candles.Select(candle => string.Create(
            CultureInfo.InvariantCulture,
            $"{candle.Symbol}|{(int)candle.Interval}|{candle.OpenTimeUtc:O}|{candle.CloseTimeUtc:O}|{candle.Open}|{candle.High}|{candle.Low}|{candle.Close}|{candle.Volume}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
