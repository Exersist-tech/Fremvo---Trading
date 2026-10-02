using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using System.Text.Json;

namespace Trading.Application.Experiments;

public sealed record ApprovedStrategyTimeframeProfile(
    CandleInterval Regime,
    CandleInterval Signal,
    CandleInterval Execution);

public static class ApprovedConsensusStrategyProfiles
{
    private static readonly Dictionary<string, ApprovedStrategyTimeframeProfile[]> s_profiles =
        new Dictionary<string, ApprovedStrategyTimeframeProfile[]>(StringComparer.Ordinal)
        {
            ["platform.ema-trend-continuation"] =
            [
                new(CandleInterval.OneHour, CandleInterval.FiveMinutes, CandleInterval.OneMinute),
                new(CandleInterval.FourHours, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes),
                new(CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.FifteenMinutes),
                new(CandleInterval.OneDay, CandleInterval.FourHours, CandleInterval.OneHour)
            ],
            ["platform.donchian-breakout-ensemble"] =
            [
                new(CandleInterval.FourHours, CandleInterval.FifteenMinutes, CandleInterval.FifteenMinutes),
                new(CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneHour),
                new(CandleInterval.OneDay, CandleInterval.FourHours, CandleInterval.FourHours)
            ],
            ["platform.bollinger-mean-reversion"] =
            [
                new(CandleInterval.FourHours, CandleInterval.FifteenMinutes, CandleInterval.FifteenMinutes),
                new(CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneHour)
            ],
            ["platform.rsi-pullback"] =
            [
                new(CandleInterval.OneHour, CandleInterval.FiveMinutes, CandleInterval.FiveMinutes),
                new(CandleInterval.FourHours, CandleInterval.FifteenMinutes, CandleInterval.FifteenMinutes),
                new(CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneHour)
            ],
            ["platform.macd-volume"] =
            [
                new(CandleInterval.FourHours, CandleInterval.FifteenMinutes, CandleInterval.FifteenMinutes),
                new(CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneHour),
                new(CandleInterval.OneDay, CandleInterval.FourHours, CandleInterval.FourHours)
            ],
            ["platform.volatility-compression-breakout"] =
            [
                new(CandleInterval.FourHours, CandleInterval.FifteenMinutes, CandleInterval.FifteenMinutes),
                new(CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneHour),
                new(CandleInterval.OneDay, CandleInterval.FourHours, CandleInterval.FourHours)
            ],
            ["platform.cross-sectional-momentum-rotation"] =
                [new(CandleInterval.OneDay, CandleInterval.OneDay, CandleInterval.FourHours)],
            ["platform.relative-strength-pullback-rotation"] =
                [new(CandleInterval.OneDay, CandleInterval.FourHours, CandleInterval.OneHour)],
            ["platform.session-conditioned-breakout"] =
            [
                new(CandleInterval.FourHours, CandleInterval.FifteenMinutes, CandleInterval.FifteenMinutes),
                new(CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneHour)
            ],
            ["platform.regime-switching-ensemble"] =
            [
                new(CandleInterval.OneDay, CandleInterval.FourHours, CandleInterval.OneHour)
            ]
        };

    public const int RequiredHistory = 220;

    public static IReadOnlyList<ApprovedStrategyTimeframeProfile> For(string familyId) =>
        s_profiles.TryGetValue(familyId, out var profiles)
            ? profiles
            : throw new ArgumentException($"'{familyId}' is not an approved consensus family.", nameof(familyId));

    public static bool TryGet(
        string familyId,
        out IReadOnlyList<ApprovedStrategyTimeframeProfile> profiles)
    {
        if (s_profiles.TryGetValue(familyId, out var found))
        {
            profiles = found;
            return true;
        }
        profiles = [];
        return false;
    }

    public static ApprovedStrategyTimeframeProfile Resolve(string familyId, CandleInterval signal) =>
        For(familyId).Single(profile => profile.Signal == signal);

    public static IReadOnlyList<CandleInterval> RequiredIntervals(string familyId, CandleInterval signal)
    {
        var profile = Resolve(familyId, signal);
        return new[] { profile.Regime, profile.Signal, profile.Execution }
            .Distinct()
            .ToArray();
    }
}

internal sealed record ExperimentStrategySeriesSet(
    ExperimentCandleSeries Regime,
    ExperimentCandleSeries Signal,
    ExperimentCandleSeries Execution)
{
    public static ExperimentStrategySeriesSet Single(ExperimentCandleSeries series) =>
        new(series, series, series);
}

internal static class ApprovedConsensusStrategyRules
{
    public static ExperimentAnalysisResult Evaluate(
        string familyId,
        ExperimentStrategySeriesSet evidence,
        string parametersJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(parametersJson))
            throw new ArgumentException("A parameter document is required.", nameof(parametersJson));
        try
        {
            using var parameters = JsonDocument.Parse(parametersJson);
            if (parameters.RootElement.ValueKind != JsonValueKind.Object
                || parameters.RootElement.EnumerateObject().Any())
            {
                return Veto(
                    familyId,
                    "strategy version 2 accepts only its immutable administrator-approved default parameter set");
            }
        }
        catch (JsonException)
        {
            return Veto(familyId, "strategy parameters are not valid JSON");
        }
        if (!ApprovedConsensusStrategyProfiles.TryGet(familyId, out var profiles)
            || !profiles.Any(profile =>
                profile.Regime == evidence.Regime.Interval
                && profile.Signal == evidence.Signal.Interval
                && profile.Execution == evidence.Execution.Interval))
        {
            return Veto(familyId, "timeframe evidence does not match an approved regime/signal/execution profile");
        }
        if (!IsSafe(evidence, out var unsafeReason))
            return Veto(familyId, unsafeReason);

        return familyId switch
        {
            "platform.ema-trend-continuation" => EmaContinuation(evidence),
            "platform.donchian-breakout-ensemble" => Donchian(evidence),
            "platform.bollinger-mean-reversion" => BollingerReversion(evidence),
            "platform.rsi-pullback" => RsiPullback(evidence),
            "platform.macd-volume" => MacdAcceleration(evidence),
            "platform.volatility-compression-breakout" => CompressionBreakout(evidence),
            "platform.session-conditioned-breakout" => SessionBreakout(evidence),
            "platform.regime-switching-ensemble" => RegimeConsensus(evidence),
            _ => ExperimentAnalysisResult.Blocked(
                $"{familyId}: complete universe evidence is required by this cross-sectional family.")
        };
    }

    private static ExperimentAnalysisResult EmaContinuation(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.ema-trend-continuation";
        if (!HasHistory(input, 201, 51, 1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var regimeFast = Ema(regime, 50);
        var regimeSlow = Ema(regime, 200);
        var priorRegimeFast = Ema(regime.Take(regime.Count - 5).ToArray(), 50);
        var signalFast = Ema(signal, 20);
        var signalMedium = Ema(signal, 50);
        var prior = signal[^2];
        var current = signal[^1];
        var volumeBaseline = signal.Skip(signal.Count - 21).Take(20).Average(candle => candle.Volume);
        var atr = Atr(signal, 14);
        if (!AtrPercentileInRange(signal, 14, atr, 5m, 100m))
            return Veto(family, "ATR percentile is outside the approved 5th-to-100th percentile entry range");

        return Consensus(family,
        [
            Check("regime-close-above-ema200", regime[^1].Close > regimeSlow, regime[^1].Close < regimeSlow,
                "Regime close must remain on the directional side of EMA 200."),
            Check("regime-ema50-above-ema200", regimeFast > regimeSlow, regimeFast < regimeSlow,
                "Regime EMA 50 must be directionally ordered against EMA 200."),
            Check("regime-ema50-slope", regimeFast > priorRegimeFast, regimeFast < priorRegimeFast,
                "EMA 50 slope is measured over five completed regime candles."),
            Check("bounded-pullback", prior.Low <= Ema(signal.Take(signal.Count - 1).ToArray(), 20)
                    && prior.Close >= signalMedium,
                prior.High >= Ema(signal.Take(signal.Count - 1).ToArray(), 20)
                    && prior.Close <= signalMedium,
                "The prior signal candle must pull toward EMA 20 without crossing EMA 50 invalidation."),
            Check("closed-resumption-with-volume", current.Close > signalFast && current.Volume >= volumeBaseline,
                current.Close < signalFast && current.Volume >= volumeBaseline,
                "The closed signal candle must resume through EMA 20 with volume at or above SMA 20.")
        ]);
    }

    private static ExperimentAnalysisResult Donchian(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.donchian-breakout-ensemble";
        if (!HasHistory(input, 201, 101, 1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var current = signal[^1];
        var prior = signal.Take(signal.Count - 1).ToArray();
        var upper20 = prior.TakeLast(20).Max(candle => candle.High);
        var lower20 = prior.TakeLast(20).Min(candle => candle.Low);
        var upper55 = prior.TakeLast(55).Max(candle => candle.High);
        var lower55 = prior.TakeLast(55).Min(candle => candle.Low);
        var upper100 = prior.TakeLast(100).Max(candle => candle.High);
        var lower100 = prior.TakeLast(100).Min(candle => candle.Low);
        var atr = Atr(signal, 14);
        var bullishExtension = current.Close - upper20;
        var bearishExtension = lower20 - current.Close;
        if ((bullishExtension > 0m && bullishExtension > atr)
            || (bearishExtension > 0m && bearishExtension > atr))
            return Veto(family, "breakout extension exceeds the approved maximum of 1 ATR");

        var ema200 = Ema(regime, 200);
        var volumeBaseline = prior.TakeLast(20).Average(candle => candle.Volume);
        return Consensus(family,
        [
            Check("donchian20", current.Close > upper20, current.Close < lower20,
                "Close must strictly break the prior 20-candle channel."),
            Check("donchian55", current.Close > upper55, current.Close < lower55,
                "Close must strictly break the prior 55-candle channel."),
            Check("donchian100", current.Close > upper100, current.Close < lower100,
                "Close must strictly break the prior 100-candle channel."),
            Check("regime-ema200", regime[^1].Close > ema200, regime[^1].Close < ema200,
                "Higher-timeframe close must be on the directional side of EMA 200."),
            Check("breakout-volume", current.Volume >= volumeBaseline * 1.1m && current.Close > upper20,
                current.Volume >= volumeBaseline * 1.1m && current.Close < lower20,
                "Breakout volume must be at least 1.1 times its preceding SMA 20.")
        ]);
    }

    private static ExperimentAnalysisResult BollingerReversion(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.bollinger-mean-reversion";
        if (!HasHistory(input, 60, 22, 1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var priorSignal = signal.Take(signal.Count - 1).ToArray();
        var prior = signal[^2];
        var current = signal[^1];
        var priorBands = Bands(priorSignal, 20, 2m);
        var currentBands = Bands(signal, 20, 2m);
        var priorRsi = Rsi(priorSignal, 14);
        var regimeEma = Ema(regime, 50);
        var previousRegimeEma = Ema(regime.Take(regime.Count - 5).ToArray(), 50);
        var slope = decimal.Abs((regimeEma - previousRegimeEma) / previousRegimeEma) * 100m;
        var adx = Adx(regime, 14);
        var bullishTrigger = current.Close > currentBands.Lower && current.Close < currentBands.Middle;
        var bearishTrigger = current.Close < currentBands.Upper && current.Close > currentBands.Middle;

        return Consensus(family,
        [
            Check("ranging-adx", adx < 25m && bullishTrigger, adx < 25m && bearishTrigger,
                "ADX 14 must remain below the approved trend threshold of 25."),
            Check("flat-ema50", slope <= 1m && bullishTrigger, slope <= 1m && bearishTrigger,
                "Absolute EMA 50 slope over five regime candles must not exceed 1%."),
            Check("prior-band-extreme", prior.Close < priorBands.Lower, prior.Close > priorBands.Upper,
                "A prior closed candle must establish an extreme outside the Bollinger band."),
            Check("prior-rsi-extreme", priorRsi <= 30m, priorRsi >= 70m,
                "RSI 14 must confirm the prior directional extreme."),
            Check("closed-band-reentry", bullishTrigger, bearishTrigger,
                "A later closed candle must re-enter the band; this is the trigger.")
        ], 5);
    }

    private static ExperimentAnalysisResult RsiPullback(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.rsi-pullback";
        if (!HasHistory(input, 201, 52, 1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var priorSignal = signal.Take(signal.Count - 1).ToArray();
        var regimeFast = Ema(regime, 50);
        var regimeSlow = Ema(regime, 200);
        var signalEma = Ema(signal, 20);
        var priorRsi = Rsi(priorSignal, 14);
        var rsi = Rsi(signal, 14);
        var current = signal[^1];
        var prior = signal[^2];
        var volumeBaseline = priorSignal.TakeLast(20).Average(candle => candle.Volume);

        return Consensus(family,
        [
            Check("regime-close-above-ema200", regime[^1].Close > regimeSlow, regime[^1].Close < regimeSlow,
                "Regime close must be on the directional side of EMA 200."),
            Check("regime-ema50-above-ema200", regimeFast > regimeSlow, regimeFast < regimeSlow,
                "Regime EMA 50 must be directionally ordered against EMA 200."),
            Check("structure-valid", current.Close > regimeSlow, current.Close < regimeSlow,
                "Signal price must remain beyond higher-timeframe structural invalidation."),
            Check("rsi-pullback-turn", priorRsi is >= 30m and <= 45m && rsi > priorRsi,
                priorRsi is >= 55m and <= 70m && rsi < priorRsi,
                "RSI must enter the bounded pullback zone and then turn on a closed candle."),
            Check("closed-price-resumption", (current.Close > signalEma || current.Close > prior.High)
                    && current.Volume >= volumeBaseline,
                (current.Close < signalEma || current.Close < prior.Low)
                    && current.Volume >= volumeBaseline,
                "Signal close must resume beyond EMA 20 or the prior extreme with acceptable volume.")
        ]);
    }

    private static ExperimentAnalysisResult MacdAcceleration(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.macd-volume";
        if (!HasHistory(input, 201, 55, 1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var prior = signal.Take(signal.Count - 1).ToArray();
        var currentMacd = Macd(signal);
        var priorMacd = Macd(prior);
        var regimeFast = Ema(regime, 50);
        var regimeSlow = Ema(regime, 200);
        var signalEma = Ema(signal, 50);
        var current = signal[^1];
        var volumeBaseline = prior.TakeLast(20).Average(candle => candle.Volume);
        var bullishMomentum = currentMacd.Line > currentMacd.Signal;
        var bearishMomentum = currentMacd.Line < currentMacd.Signal;

        return Consensus(family,
        [
            Check("bullish-regime", regimeFast > regimeSlow && regime[^1].Close > regimeSlow,
                regimeFast < regimeSlow && regime[^1].Close < regimeSlow,
                "EMA 50/200 and regime close establish direction."),
            Check("macd-cross", priorMacd.Line <= priorMacd.Signal && currentMacd.Line > currentMacd.Signal,
                priorMacd.Line >= priorMacd.Signal && currentMacd.Line < currentMacd.Signal,
                "MACD line must cross its signal on the latest closed candle."),
            Check("histogram-acceleration", currentMacd.Histogram > 0m && currentMacd.Histogram > priorMacd.Histogram,
                currentMacd.Histogram < 0m && currentMacd.Histogram < priorMacd.Histogram,
                "MACD histogram must be directional and accelerating."),
            Check("signal-ema50", current.Close > signalEma, current.Close < signalEma,
                "Signal price must be on the directional side of EMA 50."),
            Check("volume-participation", current.Volume >= volumeBaseline && bullishMomentum,
                current.Volume >= volumeBaseline && bearishMomentum,
                "Volume must meet its preceding SMA 20.")
        ]);
    }

    private static ExperimentAnalysisResult CompressionBreakout(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.volatility-compression-breakout";
        if (!HasHistory(input, 201, 121, 1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var prior = signal.Take(signal.Count - 1).ToArray();
        var current = signal[^1];
        var currentBandwidth = BandwidthPercent(prior, 20);
        var currentAtrPercent = Atr(prior, 14) / prior[^1].Close * 100m;
        var bandwidthHistory = RollingValues(prior, 20, candles => BandwidthPercent(candles, 20));
        var atrHistory = RollingValues(prior, 14, candles => Atr(candles, 14) / candles[^1].Close * 100m);
        var lowBandwidth = PercentileRank(bandwidthHistory, currentBandwidth) <= 20m;
        var lowAtr = PercentileRank(atrHistory, currentAtrPercent) <= 20m;
        var compressionPersists = Enumerable.Range(1, 5).All(offset =>
        {
            var prefix = prior.Take(prior.Length - 5 + offset).ToArray();
            return PercentileRank(bandwidthHistory, BandwidthPercent(prefix, 20)) <= 30m;
        });
        var upper = prior.TakeLast(20).Max(candle => candle.High);
        var lower = prior.TakeLast(20).Min(candle => candle.Low);
        var volumeBaseline = prior.TakeLast(20).Average(candle => candle.Volume);
        var regimeFast = Ema(regime, 50);
        var regimeSlow = Ema(regime, 200);
        var bullishBreak = current.Close > upper;
        var bearishBreak = current.Close < lower;
        if ((bullishBreak && regimeFast < regimeSlow) || (bearishBreak && regimeFast > regimeSlow))
            return Veto(family, "the higher-timeframe EMA regime opposes the breakout direction");
        var atr = Atr(signal, 14);
        if ((bullishBreak && current.Close - upper > atr) || (bearishBreak && lower - current.Close > atr))
            return Veto(family, "entry is more than 1 ATR beyond the compression boundary");

        return Consensus(family,
        [
            Check("low-bandwidth-percentile", lowBandwidth && bullishBreak, lowBandwidth && bearishBreak,
                "Bollinger bandwidth must be at or below its 20th historical percentile."),
            Check("low-atr-percentile", lowAtr && bullishBreak, lowAtr && bearishBreak,
                "ATR percentage must be at or below its 20th historical percentile."),
            Check("persistent-compression", compressionPersists && bullishBreak, compressionPersists && bearishBreak,
                "Compression must persist for five completed candles."),
            Check("closed-channel-break", bullishBreak, bearishBreak,
                "The closed signal candle must break the prior 20-candle compression boundary."),
            Check("volume-expansion", current.Volume >= volumeBaseline * 1.2m && bullishBreak,
                current.Volume >= volumeBaseline * 1.2m && bearishBreak,
                "Breakout volume must be at least 1.2 times its preceding SMA 20.")
        ]);
    }

    private static ExperimentAnalysisResult SessionBreakout(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.session-conditioned-breakout";
        if (!HasHistory(input, 201, 101, 1, out var unavailable))
            return Veto(family, unavailable);

        var signal = input.Signal.Candles;
        var current = signal[^1];
        var prior = signal.Take(signal.Count - 1).ToArray();
        var upper = prior.TakeLast(20).Max(candle => candle.High);
        var lower = prior.TakeLast(20).Min(candle => candle.Low);
        var breakout = current.Close > upper;
        var breakdown = current.Close < lower;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var local = TimeZoneInfo.ConvertTime(current.CloseTimeUtc, zone);
        var sessionActive = local.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday
            && local.TimeOfDay >= TimeSpan.FromHours(9.5)
            && local.TimeOfDay < TimeSpan.FromHours(16);
        var volumeBaseline = prior.TakeLast(20).Average(candle => candle.Volume);

        return Consensus(family,
        [
            Check("base-breakout-threshold", breakout, breakdown,
                "The base Donchian breakout must pass on a closed candle."),
            Check("versioned-session-profile", sessionActive && breakout, sessionActive && breakdown,
                "The version-1 America/New_York session profile is resolved with IANA daylight-saving rules."),
            Check("session-liquidity", current.Volume >= volumeBaseline && breakout,
                current.Volume >= volumeBaseline && breakdown,
                "Session volume must meet its preceding SMA 20."),
            Check("session-cost-gates", false, false,
                "No current session-specific spread and slippage observation was supplied."),
            Check("validated-session-baseline", false, false,
                "No immutable validation and walk-forward comparison against the no-session baseline was supplied.")
        ], 5);
    }

    private static ExperimentAnalysisResult RegimeConsensus(ExperimentStrategySeriesSet input)
    {
        const string family = "platform.regime-switching-ensemble";
        if (!HasHistory(input, 201, 101, 1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var fast = Ema(regime, 50);
        var slow = Ema(regime, 200);
        var priorFast = Ema(regime.Take(regime.Count - 5).ToArray(), 50);
        var atrPercent = Atr(regime, 14) / regime[^1].Close * 100m;
        var bandwidth = BandwidthPercent(regime, 20);
        var signalMomentum = signal[^1].Close > signal[^6].Close;
        var signalWeakness = signal[^1].Close < signal[^6].Close;
        var bullishRegime = regime[^1].Close > slow;
        var bearishRegime = regime[^1].Close < slow;
        var crisis = atrPercent >= 8m || bandwidth >= 20m;
        if (crisis)
            return Veto(family, "the deterministic regime is CRISIS and permits no new Spot exposure");

        return Consensus(family,
        [
            Check("long-term-trend", regime[^1].Close > slow, regime[^1].Close < slow,
                "Long-term direction uses the completed daily EMA 200."),
            Check("medium-trend-strength", fast > slow && fast > priorFast, fast < slow && fast < priorFast,
                "Medium-term trend uses EMA 50/200 order and EMA 50 slope."),
            Check("bounded-volatility", atrPercent is > 0m and < 8m && bullishRegime,
                atrPercent is > 0m and < 8m && bearishRegime,
                "ATR percentage must be positive and below the crisis threshold."),
            Check("non-crisis-bandwidth", bandwidth < 20m && bullishRegime,
                bandwidth < 20m && bearishRegime,
                "Bollinger bandwidth must remain below the crisis threshold."),
            Check("market-breadth-proxy", signalMomentum, signalWeakness,
                "The approved universe-breadth input must confirm positive medium-term participation.")
        ], 5);
    }

    private static ExperimentAnalysisResult Consensus(
        string family,
        IReadOnlyList<ExperimentSignalCheck> checks,
        int threshold = 4) =>
        ExperimentAnalysisResult.FromConsensus(family, checks, threshold);

    private static ExperimentAnalysisResult Veto(string family, string reason) =>
        ExperimentAnalysisResult.FromConsensus(
            family,
            Enumerable.Range(1, 5)
                .Select(index => new ExperimentSignalCheck(
                    $"unavailable-{index}",
                    ExperimentSignalDirection.Neutral,
                    reason))
                .ToArray(),
            4,
            true,
            reason);

    private static ExperimentSignalCheck Check(string id, bool bullish, bool bearish, string rationale) =>
        new(
            id,
            bullish ? ExperimentSignalDirection.Bullish
                : bearish ? ExperimentSignalDirection.Bearish
                : ExperimentSignalDirection.Neutral,
            rationale);

    private static bool HasHistory(
        ExperimentStrategySeriesSet input,
        int regime,
        int signal,
        int execution,
        out string reason)
    {
        if (input.Regime.Candles.Count < regime
            || input.Signal.Candles.Count < signal
            || input.Execution.Candles.Count < execution)
        {
            reason = $"requires at least {regime} regime, {signal} signal, and {execution} execution closed candles";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool IsSafe(ExperimentStrategySeriesSet input, out string reason)
    {
        var all = new[] { input.Regime, input.Signal, input.Execution };
        if (all.Any(series => series.AsOfUtc.Offset != TimeSpan.Zero
            || series.Candles.Count == 0
            || series.Candles[^1].CloseTimeUtc != series.AsOfUtc
            || series.Candles.Any(candle => !candle.CanBeUsedForClosedCandleSignal
                || candle.OpenTimeUtc.Offset != TimeSpan.Zero
                || candle.CloseTimeUtc.Offset != TimeSpan.Zero
                || candle.CloseTimeUtc > series.AsOfUtc)
            || series.Candles.Zip(series.Candles.Skip(1), static (left, right) =>
                    left.CloseTimeUtc == right.OpenTimeUtc)
                .Any(contiguous => !contiguous)))
        {
            reason = "all timeframe evidence must be contiguous, safe, completed, and UTC aligned";
            return false;
        }
        if (all.Select(series => series.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            reason = "all timeframe roles must refer to the same symbol";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static decimal Ema(IReadOnlyList<Candle> candles, int period) =>
        new ExponentialMovingAverageCalculator(period).Calculate(candles).Value
        ?? throw new InvalidOperationException("EMA warm-up was checked before calculation.");

    private static decimal Rsi(IReadOnlyList<Candle> candles, int period) =>
        new RelativeStrengthIndexCalculator(period).Calculate(candles).Value
        ?? throw new InvalidOperationException("RSI warm-up was checked before calculation.");

    private static decimal Atr(IReadOnlyList<Candle> candles, int period) =>
        new AverageTrueRangeCalculator(period).Calculate(candles).Value
        ?? throw new InvalidOperationException("ATR warm-up was checked before calculation.");

    private static MacdValue Macd(IReadOnlyList<Candle> candles) =>
        new MovingAverageConvergenceDivergenceCalculator(12, 26, 9).Calculate(candles).Value
        ?? throw new InvalidOperationException("MACD warm-up was checked before calculation.");

    private static BollingerBandsValue Bands(IReadOnlyList<Candle> candles, int period, decimal multiplier) =>
        new BollingerBandsCalculator(period, multiplier).Calculate(candles).Value
        ?? throw new InvalidOperationException("Bollinger warm-up was checked before calculation.");

    private static decimal BandwidthPercent(IReadOnlyList<Candle> candles, int period)
    {
        var bands = Bands(candles, period, 2m);
        return bands.Middle <= 0m ? decimal.MaxValue : (bands.Upper - bands.Lower) / bands.Middle * 100m;
    }

    private static decimal[] RollingValues(
        IReadOnlyList<Candle> candles,
        int minimum,
        Func<IReadOnlyList<Candle>, decimal> selector) =>
        Enumerable.Range(minimum, candles.Count - minimum + 1)
            .Select(count => selector(candles.Take(count).ToArray()))
            .ToArray();

    private static decimal PercentileRank(decimal[] values, decimal value) =>
        values.Length == 0 ? 100m : values.Count(candidate => candidate <= value) * 100m / values.Length;

    private static bool AtrPercentileInRange(
        IReadOnlyList<Candle> candles,
        int period,
        decimal value,
        decimal minimum,
        decimal maximum)
    {
        var history = RollingValues(candles, period + 1, prefix => Atr(prefix, period));
        var percentile = PercentileRank(history, value);
        return percentile >= minimum && percentile <= maximum;
    }

    private static decimal Adx(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period * 2 + 1)
            return decimal.MaxValue;
        var trueRanges = new List<decimal>();
        var plus = new List<decimal>();
        var minus = new List<decimal>();
        for (var index = 1; index < candles.Count; index++)
        {
            var current = candles[index];
            var previous = candles[index - 1];
            trueRanges.Add(Math.Max(
                current.High - current.Low,
                Math.Max(decimal.Abs(current.High - previous.Close), decimal.Abs(current.Low - previous.Close))));
            var up = current.High - previous.High;
            var down = previous.Low - current.Low;
            plus.Add(up > down && up > 0m ? up : 0m);
            minus.Add(down > up && down > 0m ? down : 0m);
        }
        var dx = new List<decimal>();
        for (var end = period; end <= trueRanges.Count; end++)
        {
            var tr = trueRanges.Skip(end - period).Take(period).Sum();
            if (tr <= 0m)
            {
                dx.Add(0m);
                continue;
            }
            var plusDi = plus.Skip(end - period).Take(period).Sum() / tr * 100m;
            var minusDi = minus.Skip(end - period).Take(period).Sum() / tr * 100m;
            dx.Add(plusDi + minusDi == 0m
                ? 0m
                : decimal.Abs(plusDi - minusDi) / (plusDi + minusDi) * 100m);
        }
        return dx.TakeLast(period).Average();
    }
}
