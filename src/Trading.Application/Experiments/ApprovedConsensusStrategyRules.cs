using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Strategies;
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
            ],
            ["platform.three-swing-channel-divergence"] =
            [
                new(CandleInterval.FourHours, CandleInterval.FiveMinutes, CandleInterval.FiveMinutes)
            ]
        };

    public const int RequiredHistory = 320;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        var profile = Resolve(familyId, signal);
        var required = new[] { profile.Regime, profile.Signal, profile.Execution };
        if (familyId.Equals("platform.three-swing-channel-divergence", StringComparison.Ordinal))
            required = [.. required, CandleInterval.OneHour];
        return required
            .Distinct()
            .ToArray();
    }
}

internal sealed record ExperimentStrategySeriesSet(
    ExperimentCandleSeries Regime,
    ExperimentCandleSeries Signal,
    ExperimentCandleSeries Execution,
    ExperimentCandleSeries? HourlyContext = null)
{
    public static ExperimentStrategySeriesSet Single(ExperimentCandleSeries series) =>
        new(series, series, series);
}

internal static class ApprovedConsensusStrategyRules
{
    public static ExperimentAnalysisResult Evaluate(
        string familyId,
        ExperimentStrategySeriesSet evidence,
        string parametersJson,
        int strategyVersion = 3)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyId);
        ArgumentNullException.ThrowIfNull(evidence);
        if (strategyVersion is not (2 or 3 or 4 or 5))
            throw new ArgumentOutOfRangeException(nameof(strategyVersion));
        if (string.IsNullOrWhiteSpace(parametersJson))
            throw new ArgumentException("A parameter document is required.", nameof(parametersJson));
        if (!ApprovedStrategyParameters.TryNormalize(familyId, parametersJson, out var normalizedParameters, out var parameterError))
            return Veto(familyId, parameterError);
        if (!ApprovedConsensusStrategyProfiles.TryGet(familyId, out var profiles)
            || !profiles.Any(profile =>
                profile.Regime == evidence.Regime.Interval
                && profile.Signal == evidence.Signal.Interval
                && profile.Execution == evidence.Execution.Interval)
            || (familyId == "platform.three-swing-channel-divergence"
                && evidence.HourlyContext?.Interval != CandleInterval.OneHour))
        {
            return Veto(familyId, "timeframe evidence does not match an approved regime/signal/execution profile");
        }
        if (!IsSafe(evidence, out var unsafeReason))
            return Veto(familyId, unsafeReason);
        if (familyId == "platform.regime-switching-ensemble" && strategyVersion >= 4)
            return Veto(familyId, "the selected component and complete eligible-universe breadth must be replayed together");

        var result = familyId switch
        {
            "platform.ema-trend-continuation" => EmaContinuation(evidence, normalizedParameters),
            "platform.donchian-breakout-ensemble" => Donchian(evidence, normalizedParameters),
            "platform.bollinger-mean-reversion" => BollingerReversion(evidence, normalizedParameters),
            "platform.rsi-pullback" => RsiPullback(evidence, normalizedParameters),
            "platform.macd-volume" => MacdAcceleration(evidence, normalizedParameters),
            "platform.volatility-compression-breakout" => CompressionBreakout(
                evidence, normalizedParameters, strategyVersion >= 4),
            "platform.session-conditioned-breakout" => SessionBreakout(evidence, normalizedParameters),
            "platform.regime-switching-ensemble" => RegimeConsensus(evidence, normalizedParameters),
            "platform.three-swing-channel-divergence" => ThreeSwingDivergence(evidence, normalizedParameters),
            _ => ExperimentAnalysisResult.Blocked(
                $"{familyId}: complete universe evidence is required by this cross-sectional family.")
        };
        if (strategyVersion == 2 && result.Consensus is { MandatoryVeto: false, RequiredEntryChecks.Count: > 0 } legacy)
            return ExperimentAnalysisResult.FromConsensus(familyId,
                legacy.Checks.Select(check => check with
                {
                    Id = familyId switch
                    {
                        "platform.donchian-breakout-ensemble"
                            when check.Id.StartsWith("regime-ema", StringComparison.Ordinal) => "regime-ema200",
                        "platform.bollinger-mean-reversion"
                            when check.Id.StartsWith("flat-ema", StringComparison.Ordinal) => "flat-ema50",
                        "platform.rsi-pullback"
                            when check.Id.StartsWith("regime-close-above-ema", StringComparison.Ordinal) => "regime-close-above-ema200",
                        "platform.rsi-pullback"
                            when check.Id.StartsWith("regime-ema", StringComparison.Ordinal) => "regime-ema50-above-ema200",
                        _ => check.Id
                    }
                }).ToArray(), legacy.RequiredAgreement);
        return result;
    }

    private static ExperimentAnalysisResult EmaContinuation(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.ema-trend-continuation";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var regimeFastPeriod = parameters.Int32("regimeFastEma");
        var regimeSlowPeriod = parameters.Int32("regimeSlowEma");
        var slopeLookback = parameters.Int32("regimeSlopeLookback");
        var pullbackPeriod = parameters.Int32("signalPullbackEma");
        var invalidationPeriod = parameters.Int32("signalInvalidationEma");
        var volumePeriod = parameters.Int32("volumePeriod");
        var atrPeriod = parameters.Int32("atrPeriod");
        if (!HasHistory(input, Math.Max(regimeSlowPeriod + 1, regimeFastPeriod + slopeLookback),
                new[] { invalidationPeriod + 1, volumePeriod + 1, pullbackPeriod + 1, atrPeriod + 1 }.Max(),
                1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var regimeFast = Ema(regime, regimeFastPeriod);
        var regimeSlow = Ema(regime, regimeSlowPeriod);
        var priorRegimeFast = Ema(regime.Take(regime.Count - slopeLookback).ToArray(), regimeFastPeriod);
        var signalFast = Ema(signal, pullbackPeriod);
        var signalMedium = Ema(signal, invalidationPeriod);
        var prior = signal[^2];
        var current = signal[^1];
        var volumeBaseline = signal.Skip(signal.Count - volumePeriod - 1).Take(volumePeriod).Average(candle => candle.Volume);
        var atr = Atr(signal, atrPeriod);
        if (!AtrPercentileInRange(signal, atrPeriod, atr,
                parameters.Decimal("minimumAtrPercentile"), parameters.Decimal("maximumAtrPercentile")))
            return Veto(family, "ATR percentile is outside the configured entry range");

        return Consensus(family,
        [
            Check($"regime-close-above-ema{regimeSlowPeriod}", regime[^1].Close > regimeSlow, regime[^1].Close < regimeSlow,
                $"Regime close must remain on the directional side of EMA {regimeSlowPeriod}."),
            Check($"regime-ema{regimeFastPeriod}-above-ema{regimeSlowPeriod}", regimeFast > regimeSlow, regimeFast < regimeSlow,
                $"Regime EMA {regimeFastPeriod} must be directionally ordered against EMA {regimeSlowPeriod}."),
            Check($"regime-ema{regimeFastPeriod}-slope", regimeFast > priorRegimeFast, regimeFast < priorRegimeFast,
                $"EMA {regimeFastPeriod} slope is measured over {slopeLookback} completed regime candles."),
            Check("bounded-pullback", prior.Low <= Ema(signal.Take(signal.Count - 1).ToArray(), pullbackPeriod)
                    && prior.Close >= signalMedium,
                prior.High >= Ema(signal.Take(signal.Count - 1).ToArray(), pullbackPeriod)
                    && prior.Close <= signalMedium,
                $"The prior signal candle must pull toward EMA {pullbackPeriod} without crossing EMA {invalidationPeriod} invalidation."),
            Check("closed-resumption-with-volume", current.Close > signalFast && current.Volume >= volumeBaseline,
                current.Close < signalFast && current.Volume >= volumeBaseline,
                $"The closed signal candle must resume through EMA {pullbackPeriod} with volume at or above SMA {volumePeriod}.")
        ], parameters.Int32("minimumAgreement"),
            ["regime-close-above-ema" + regimeSlowPeriod, "regime-ema" + regimeFastPeriod + "-above-ema" + regimeSlowPeriod,
                "bounded-pullback", "closed-resumption-with-volume"]);
    }

    private static ExperimentAnalysisResult Donchian(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.donchian-breakout-ensemble";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var shortPeriod = parameters.Int32("shortChannel");
        var mediumPeriod = parameters.Int32("mediumChannel");
        var longPeriod = parameters.Int32("longChannel");
        var volumePeriod = parameters.Int32("volumePeriod");
        var regimeEmaPeriod = parameters.Int32("regimeEma");
        var atrPeriod = parameters.Int32("atrPeriod");
        if (!HasHistory(input, regimeEmaPeriod + 1,
                new[] { longPeriod + 1, volumePeriod + 1, atrPeriod + 1 }.Max(),
                1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var current = signal[^1];
        var prior = signal.Take(signal.Count - 1).ToArray();
        var upper20 = prior.TakeLast(shortPeriod).Max(candle => candle.High);
        var lower20 = prior.TakeLast(shortPeriod).Min(candle => candle.Low);
        var upper55 = prior.TakeLast(mediumPeriod).Max(candle => candle.High);
        var lower55 = prior.TakeLast(mediumPeriod).Min(candle => candle.Low);
        var upper100 = prior.TakeLast(longPeriod).Max(candle => candle.High);
        var lower100 = prior.TakeLast(longPeriod).Min(candle => candle.Low);
        var atr = Atr(signal, atrPeriod);
        var bullishExtension = current.Close - upper20;
        var bearishExtension = lower20 - current.Close;
        if ((bullishExtension > 0m && bullishExtension > atr * parameters.Decimal("maximumExtensionAtr"))
            || (bearishExtension > 0m && bearishExtension > atr * parameters.Decimal("maximumExtensionAtr")))
            return Veto(family, "breakout extension exceeds the configured maximum ATR distance");

        var ema200 = Ema(regime, regimeEmaPeriod);
        var volumeBaseline = prior.TakeLast(volumePeriod).Average(candle => candle.Volume);
        return Consensus(family,
        [
            Check($"donchian{shortPeriod}", current.Close > upper20, current.Close < lower20,
                $"Close must strictly break the prior {shortPeriod}-candle channel."),
            Check($"donchian{mediumPeriod}", current.Close > upper55, current.Close < lower55,
                $"Close must strictly break the prior {mediumPeriod}-candle channel."),
            Check($"donchian{longPeriod}", current.Close > upper100, current.Close < lower100,
                $"Close must strictly break the prior {longPeriod}-candle channel."),
            Check($"regime-ema{regimeEmaPeriod}", regime[^1].Close > ema200, regime[^1].Close < ema200,
                $"Higher-timeframe close must be on the directional side of EMA {regimeEmaPeriod}."),
            Check("breakout-volume", current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && current.Close > upper20,
                current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && current.Close < lower20,
                $"Breakout volume must meet its {parameters.Decimal("volumeMultiplier")}x preceding SMA {volumePeriod}.")
        ], parameters.Int32("minimumAgreement"),
            ["donchian" + shortPeriod, "regime-ema" + regimeEmaPeriod, "breakout-volume"]);
    }

    private static ExperimentAnalysisResult BollingerReversion(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.bollinger-mean-reversion";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var bandPeriod = parameters.Int32("bollingerPeriod");
        var rsiPeriod = parameters.Int32("rsiPeriod");
        var adxPeriod = parameters.Int32("adxPeriod");
        var emaPeriod = parameters.Int32("flatEmaPeriod");
        var slopeLookback = parameters.Int32("slopeLookback");
        if (!HasHistory(input,
                new[] { emaPeriod + slopeLookback, adxPeriod * 2 + 1 }.Max(),
                new[] { bandPeriod + 2, rsiPeriod + 1 }.Max(),
                1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var priorSignal = signal.Take(signal.Count - 1).ToArray();
        var prior = signal[^2];
        var current = signal[^1];
        var deviation = parameters.Decimal("bollingerDeviation");
        var priorBands = Bands(priorSignal, bandPeriod, deviation);
        var currentBands = Bands(signal, bandPeriod, deviation);
        var priorRsi = Rsi(priorSignal, rsiPeriod);
        var regimeEma = Ema(regime, emaPeriod);
        var previousRegimeEma = Ema(regime.Take(regime.Count - slopeLookback).ToArray(), emaPeriod);
        var slope = decimal.Abs((regimeEma - previousRegimeEma) / previousRegimeEma) * 100m;
        var adx = Adx(regime, adxPeriod);
        var bullishTrigger = current.Close > currentBands.Lower && current.Close < currentBands.Middle;
        var bearishTrigger = current.Close < currentBands.Upper && current.Close > currentBands.Middle;

        return Consensus(family,
        [
            Check("ranging-adx", adx < parameters.Decimal("maximumAdx") && bullishTrigger, adx < parameters.Decimal("maximumAdx") && bearishTrigger,
                $"ADX {adxPeriod} must remain below {parameters.Decimal("maximumAdx")}."),
            Check($"flat-ema{emaPeriod}", slope <= parameters.Decimal("maximumEmaSlopePercent") && bullishTrigger,
                slope <= parameters.Decimal("maximumEmaSlopePercent") && bearishTrigger,
                $"Absolute EMA {emaPeriod} slope over {slopeLookback} regime candles must not exceed {parameters.Decimal("maximumEmaSlopePercent")}%."),
            Check("prior-band-extreme", prior.Close < priorBands.Lower, prior.Close > priorBands.Upper,
                "A prior closed candle must establish an extreme outside the Bollinger band."),
            Check("prior-rsi-extreme", priorRsi <= parameters.Decimal("oversoldRsi"),
                priorRsi >= parameters.Decimal("overboughtRsi"),
                $"RSI {rsiPeriod} must confirm the configured directional extreme."),
            Check("closed-band-reentry", bullishTrigger, bearishTrigger,
                "A later closed candle must re-enter the band; this is the trigger.")
        ], parameters.Int32("minimumAgreement"),
            ["ranging-adx", "flat-ema" + emaPeriod, "prior-band-extreme", "prior-rsi-extreme", "closed-band-reentry"]);
    }

    private static ExperimentAnalysisResult RsiPullback(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.rsi-pullback";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var regimeFastPeriod = parameters.Int32("regimeFastEma");
        var regimeSlowPeriod = parameters.Int32("regimeSlowEma");
        var signalEmaPeriod = parameters.Int32("signalEma");
        var rsiPeriod = parameters.Int32("rsiPeriod");
        var volumePeriod = parameters.Int32("volumePeriod");
        if (!HasHistory(input, regimeSlowPeriod + 1,
                new[] { signalEmaPeriod + 1, volumePeriod + 1, rsiPeriod + 1 }.Max(),
                1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var priorSignal = signal.Take(signal.Count - 1).ToArray();
        var regimeFast = Ema(regime, regimeFastPeriod);
        var regimeSlow = Ema(regime, regimeSlowPeriod);
        var signalEma = Ema(signal, signalEmaPeriod);
        var priorRsi = Rsi(priorSignal, rsiPeriod);
        var rsi = Rsi(signal, rsiPeriod);
        var current = signal[^1];
        var prior = signal[^2];
        var volumeBaseline = priorSignal.TakeLast(volumePeriod).Average(candle => candle.Volume);

        return Consensus(family,
        [
            Check($"regime-close-above-ema{regimeSlowPeriod}", regime[^1].Close > regimeSlow, regime[^1].Close < regimeSlow,
                $"Regime close must be on the directional side of EMA {regimeSlowPeriod}."),
            Check($"regime-ema{regimeFastPeriod}-above-ema{regimeSlowPeriod}", regimeFast > regimeSlow, regimeFast < regimeSlow,
                $"Regime EMA {regimeFastPeriod} must be directionally ordered against EMA {regimeSlowPeriod}."),
            Check("structure-valid", current.Close > regimeSlow, current.Close < regimeSlow,
                $"Signal price must remain beyond higher-timeframe EMA {regimeSlowPeriod} structural invalidation."),
            Check("rsi-pullback-turn",
                priorRsi >= parameters.Decimal("longRsiMinimum") && priorRsi <= parameters.Decimal("longRsiMaximum") && rsi > priorRsi,
                priorRsi >= parameters.Decimal("shortRsiMinimum") && priorRsi <= parameters.Decimal("shortRsiMaximum") && rsi < priorRsi,
                $"RSI {rsiPeriod} must enter the configured pullback zone and then turn on a closed candle."),
            Check("closed-price-resumption", (current.Close > signalEma || current.Close > prior.High)
                    && current.Volume >= volumeBaseline,
                (current.Close < signalEma || current.Close < prior.Low)
                    && current.Volume >= volumeBaseline,
                $"Signal close must resume beyond EMA {signalEmaPeriod} or the prior extreme with volume meeting SMA {volumePeriod}.")
        ], parameters.Int32("minimumAgreement"),
            ["regime-ema" + regimeFastPeriod + "-above-ema" + regimeSlowPeriod,
                "structure-valid", "rsi-pullback-turn", "closed-price-resumption"]);
    }

    private static ExperimentAnalysisResult MacdAcceleration(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.macd-volume";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var regimeFastPeriod = parameters.Int32("regimeFastEma");
        var regimeSlowPeriod = parameters.Int32("regimeSlowEma");
        var macdFastPeriod = parameters.Int32("macdFast");
        var macdSlowPeriod = parameters.Int32("macdSlow");
        var macdSignalPeriod = parameters.Int32("macdSignal");
        var trendEmaPeriod = parameters.Int32("signalEma");
        var volumePeriod = parameters.Int32("volumePeriod");
        if (!HasHistory(input, regimeSlowPeriod + 1,
                new[] { macdSlowPeriod + macdSignalPeriod, volumePeriod + 1, trendEmaPeriod + 1 }.Max(),
                1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var prior = signal.Take(signal.Count - 1).ToArray();
        var currentMacd = Macd(signal, macdFastPeriod, macdSlowPeriod, macdSignalPeriod);
        var priorMacd = Macd(prior, macdFastPeriod, macdSlowPeriod, macdSignalPeriod);
        var regimeFast = Ema(regime, regimeFastPeriod);
        var regimeSlow = Ema(regime, regimeSlowPeriod);
        var signalEma = Ema(signal, trendEmaPeriod);
        var current = signal[^1];
        var volumeBaseline = prior.TakeLast(volumePeriod).Average(candle => candle.Volume);
        var bullishMomentum = currentMacd.Line > currentMacd.Signal;
        var bearishMomentum = currentMacd.Line < currentMacd.Signal;

        return Consensus(family,
        [
            Check("bullish-regime", regimeFast > regimeSlow && regime[^1].Close > regimeSlow,
                regimeFast < regimeSlow && regime[^1].Close < regimeSlow,
                $"EMA {regimeFastPeriod}/{regimeSlowPeriod} and regime close establish direction."),
            Check("macd-cross", priorMacd.Line <= priorMacd.Signal && currentMacd.Line > currentMacd.Signal,
                priorMacd.Line >= priorMacd.Signal && currentMacd.Line < currentMacd.Signal,
                "MACD line must cross its signal on the latest closed candle."),
            Check("histogram-acceleration", currentMacd.Histogram > 0m && currentMacd.Histogram > priorMacd.Histogram,
                currentMacd.Histogram < 0m && currentMacd.Histogram < priorMacd.Histogram,
                "MACD histogram must be directional and accelerating."),
            Check($"signal-ema{trendEmaPeriod}", current.Close > signalEma, current.Close < signalEma,
                $"Signal price must be on the directional side of EMA {trendEmaPeriod}."),
            Check("volume-participation", current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && bullishMomentum,
                current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && bearishMomentum,
                $"Volume must meet its preceding SMA {volumePeriod} times {parameters.Decimal("volumeMultiplier")}.")
        ], parameters.Int32("minimumAgreement"),
            ["bullish-regime", "macd-cross", "volume-participation"]);
    }

    private static ExperimentAnalysisResult CompressionBreakout(
        ExperimentStrategySeriesSet input, string parametersJson, bool pastOnlyPercentiles)
    {
        const string family = "platform.volatility-compression-breakout";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var regimeSlowPeriod = parameters.Int32("regimeSlowEma");
        var channelPeriod = parameters.Int32("channelPeriod");
        var volumePeriod = parameters.Int32("volumePeriod");
        var persistenceCandles = parameters.Int32("persistenceCandles");
        var bandPeriod = parameters.Int32("bollingerPeriod");
        var atrPeriod = parameters.Int32("atrPeriod");
        const int minimumPercentileObservations = 100;
        var percentileWarmup = pastOnlyPercentiles
            ? Math.Max(bandPeriod + persistenceCandles + minimumPercentileObservations,
                atrPeriod + 2 + minimumPercentileObservations)
            : Math.Max(bandPeriod, atrPeriod + 1);
        if (!HasHistory(input, regimeSlowPeriod + 1,
                new[] { channelPeriod + 1, volumePeriod + 1, percentileWarmup }.Max(),
                1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var prior = signal.Take(signal.Count - 1).ToArray();
        var current = signal[^1];
        var deviation = parameters.Decimal("bollingerDeviation");
        var currentBandwidth = BandwidthPercent(prior, bandPeriod, deviation);
        var currentAtrPercent = Atr(prior, atrPeriod) / prior[^1].Close * 100m;
        var bandwidthHistory = RollingValues(prior, bandPeriod, candles => BandwidthPercent(candles, bandPeriod, deviation));
        var atrHistory = RollingValues(prior, atrPeriod + 1, candles => Atr(candles, atrPeriod) / candles[^1].Close * 100m);
        var percentile = parameters.Decimal("compressionPercentile");
        var lowBandwidth = PercentileRank(
            pastOnlyPercentiles ? bandwidthHistory[..^1] : bandwidthHistory, currentBandwidth) <= percentile;
        var lowAtr = PercentileRank(
            pastOnlyPercentiles ? atrHistory[..^1] : atrHistory, currentAtrPercent) <= percentile;
        var persistencePercentile = parameters.Decimal("persistencePercentile");
        var compressionPersists = HasPersistentCompression(
            bandwidthHistory, persistenceCandles, persistencePercentile, pastOnlyPercentiles);
        var upper = prior.TakeLast(channelPeriod).Max(candle => candle.High);
        var lower = prior.TakeLast(channelPeriod).Min(candle => candle.Low);
        var volumeBaseline = prior.TakeLast(volumePeriod).Average(candle => candle.Volume);
        var regimeFast = Ema(regime, parameters.Int32("regimeFastEma"));
        var regimeSlow = Ema(regime, regimeSlowPeriod);
        var bullishBreak = current.Close > upper;
        var bearishBreak = current.Close < lower;
        if ((bullishBreak && regimeFast < regimeSlow) || (bearishBreak && regimeFast > regimeSlow))
            return Veto(family, "the higher-timeframe EMA regime opposes the breakout direction");
        var atr = Atr(signal, atrPeriod);
        var maximumExtension = atr * parameters.Decimal("maximumExtensionAtr");
        if ((bullishBreak && current.Close - upper > maximumExtension)
            || (bearishBreak && lower - current.Close > maximumExtension))
            return Veto(family, "entry extends beyond the configured ATR distance from the compression boundary");

        return Consensus(family,
        [
            Check("low-bandwidth-percentile", lowBandwidth && bullishBreak, lowBandwidth && bearishBreak,
                $"Bollinger bandwidth must be at or below its {percentile}th historical percentile."),
            Check("low-atr-percentile", lowAtr && bullishBreak, lowAtr && bearishBreak,
                $"ATR percentage must be at or below its {percentile}th historical percentile."),
            Check("persistent-compression", compressionPersists && bullishBreak, compressionPersists && bearishBreak,
                $"Compression must persist for {persistenceCandles} completed candles."),
            Check("closed-channel-break", bullishBreak, bearishBreak,
                $"The closed signal candle must break the prior {channelPeriod}-candle compression boundary."),
            Check("volume-expansion", current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && bullishBreak,
                current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && bearishBreak,
                $"Breakout volume must meet {parameters.Decimal("volumeMultiplier")} times its preceding SMA {volumePeriod}.")
        ], parameters.Int32("minimumAgreement"),
            ["low-bandwidth-percentile", "persistent-compression", "closed-channel-break", "volume-expansion"]);
    }

    private static ExperimentAnalysisResult SessionBreakout(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.session-conditioned-breakout";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var channelPeriod = parameters.Int32("channelPeriod");
        var volumePeriod = parameters.Int32("volumePeriod");
        if (!HasHistory(input, 201, Math.Max(channelPeriod + 1, volumePeriod + 1), 1, out var unavailable))
            return Veto(family, unavailable);

        var signal = input.Signal.Candles;
        var current = signal[^1];
        var prior = signal.Take(signal.Count - 1).ToArray();
        var upper = prior.TakeLast(channelPeriod).Max(candle => candle.High);
        var lower = prior.TakeLast(channelPeriod).Min(candle => candle.Low);
        var breakout = current.Close > upper;
        var breakdown = current.Close < lower;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(parameters.String("sessionTimeZone"));
        var local = TimeZoneInfo.ConvertTime(current.CloseTimeUtc, zone);
        var localTime = local.TimeOfDay;
        var start = new TimeSpan(parameters.Int32("sessionStartHour"), parameters.Int32("sessionStartMinute"), 0);
        var end = new TimeSpan(parameters.Int32("sessionEndHour"), parameters.Int32("sessionEndMinute"), 0);
        var sessionActive = (!parameters.Boolean("weekdaysOnly")
                || local.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday)
            && localTime >= start && localTime < end;
        var volumeBaseline = prior.TakeLast(volumePeriod).Average(candle => candle.Volume);

        return Consensus(family,
        [
            Check("base-breakout-threshold", breakout, breakdown,
                $"The base Donchian {channelPeriod}-candle breakout must pass on a closed candle."),
            Check("versioned-session-profile", sessionActive && breakout, sessionActive && breakdown,
                $"The configured {parameters.String("sessionTimeZone")} session window is resolved with IANA daylight-saving rules."),
            Check("session-liquidity", current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && breakout,
                current.Volume >= volumeBaseline * parameters.Decimal("volumeMultiplier") && breakdown,
                $"Session volume must meet {parameters.Decimal("volumeMultiplier")} times its preceding SMA {volumePeriod}."),
            Check("session-cost-gates", false, false,
                "No current session-specific spread and slippage observation was supplied."),
            Check("validated-session-baseline", false, false,
                "No immutable validation and walk-forward comparison against the no-session baseline was supplied.")
        ], parameters.Int32("minimumAgreement"),
            ["base-breakout-threshold", "versioned-session-profile", "session-liquidity", "session-cost-gates", "validated-session-baseline"]);
    }

    private static ExperimentAnalysisResult RegimeConsensus(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.regime-switching-ensemble";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var slowPeriod = parameters.Int32("regimeSlowEma");
        var fastPeriod = parameters.Int32("regimeFastEma");
        var slopeLookback = parameters.Int32("slopeLookback");
        var bollingerPeriod = parameters.Int32("bollingerPeriod");
        var momentumLookback = parameters.Int32("signalMomentumLookback");
        if (!HasHistory(input,
                new[] { slowPeriod + 1, fastPeriod + slopeLookback, bollingerPeriod + 1 }.Max(),
                Math.Max(momentumLookback + 1, bollingerPeriod + 1),
                1, out var unavailable))
            return Veto(family, unavailable);

        var regime = input.Regime.Candles;
        var signal = input.Signal.Candles;
        var fast = Ema(regime, fastPeriod);
        var slow = Ema(regime, slowPeriod);
        var priorFast = Ema(regime.Take(regime.Count - slopeLookback).ToArray(), fastPeriod);
        var atrPercent = Atr(regime, parameters.Int32("atrPeriod")) / regime[^1].Close * 100m;
        var bandwidth = BandwidthPercent(regime, bollingerPeriod, parameters.Decimal("bollingerDeviation"));
        var signalMomentum = signal[^1].Close > signal[^(momentumLookback + 1)].Close;
        var signalWeakness = signal[^1].Close < signal[^(momentumLookback + 1)].Close;
        var bullishRegime = regime[^1].Close > slow;
        var bearishRegime = regime[^1].Close < slow;
        var crisis = atrPercent >= parameters.Decimal("crisisAtrPercent")
            || bandwidth >= parameters.Decimal("crisisBandwidthPercent");
        if (crisis)
            return Veto(family, "the deterministic regime is CRISIS and permits no new Spot exposure");

        return Consensus(family,
        [
            Check($"long-term-trend-ema{slowPeriod}", regime[^1].Close > slow, regime[^1].Close < slow,
                $"Long-term direction uses the completed daily EMA {slowPeriod}."),
            Check("medium-trend-strength", fast > slow && fast > priorFast, fast < slow && fast < priorFast,
                $"Medium-term trend uses EMA {fastPeriod}/{slowPeriod} order and EMA {fastPeriod} slope."),
            Check("bounded-volatility", atrPercent is > 0m && atrPercent < parameters.Decimal("crisisAtrPercent") && bullishRegime,
                atrPercent is > 0m && atrPercent < parameters.Decimal("crisisAtrPercent") && bearishRegime,
                $"ATR percentage must be positive and below {parameters.Decimal("crisisAtrPercent")}%."),
            Check("non-crisis-bandwidth", bandwidth < parameters.Decimal("crisisBandwidthPercent") && bullishRegime,
                bandwidth < parameters.Decimal("crisisBandwidthPercent") && bearishRegime,
                $"Bollinger bandwidth must remain below {parameters.Decimal("crisisBandwidthPercent")}%."),
            Check("market-breadth-proxy", signalMomentum, signalWeakness,
                $"The closed signal price must confirm the direction over {momentumLookback} candles.")
        ], parameters.Int32("minimumAgreement"),
            ["long-term-trend-ema" + slowPeriod, "medium-trend-strength", "bounded-volatility", "non-crisis-bandwidth", "market-breadth-proxy"]);
    }

    private static ExperimentAnalysisResult ThreeSwingDivergence(ExperimentStrategySeriesSet input, string parametersJson)
    {
        const string family = "platform.three-swing-channel-divergence";
        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        var channelPeriod = parameters.Int32("channelPeriod");
        var pivotLookback = parameters.Int32("maximumPivotLookback");
        if (!HasHistory(input, Math.Max(50, channelPeriod), Math.Max(60, pivotLookback), 1, out var unavailable))
            return Veto(family, unavailable);
        if (input.HourlyContext is null || input.HourlyContext.Candles.Count < channelPeriod)
            return Veto(family, $"at least {channelPeriod} safe, completed 1-hour context candles are required");

        var evidence = ThreeSwingChannelDivergenceModel.Evaluate(
            input.Signal.Candles,
            input.HourlyContext.Candles,
            input.Regime.Candles,
            parameters.Int32("rsiPeriod"),
            parameters.Int32("macdFast"),
            parameters.Int32("macdSlow"),
            parameters.Int32("macdSignal"),
            channelPeriod,
            parameters.Int32("pivotSideBars"),
            parameters.Int32("maximumPivotLookback"),
            parameters.Decimal("channelProximityPercent"),
            parameters.Decimal("contextBearishMinimumPercent"),
            parameters.Decimal("contextBullishMaximumPercent"),
            parameters.Decimal("minimumRsiDivergencePoints"),
            parameters.Decimal("minimumPriceProgressPercent"));
        if (!evidence.IsAvailable)
            return Veto(family, evidence.Reason);

        var bullish = evidence.Direction == ThreeSwingDivergenceDirection.Bullish;
        var bearish = evidence.Direction == ThreeSwingDivergenceDirection.Bearish;
        var alignedContexts = (evidence.IsOneHourContextAligned ? 1 : 0)
            + (evidence.IsFourHourContextAligned ? 1 : 0);
        var contextAligned = alignedContexts >= parameters.Int32("minimumAlignedContextTimeframes");
        var contextRationale = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"1h channel position {evidence.OneHourChannelPositionPercent?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}%; 4h channel position {evidence.FourHourChannelPositionPercent?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}%.");

        var requiredChecks = new List<string>
        {
            "three-swing-rsi-divergence",
            "five-minute-channel-proximity",
            "one-hour-four-hour-context"
        };
        if (parameters.Boolean("requireClosedReversal"))
            requiredChecks.Add("closed-reversal-confirmation");
        if (parameters.Boolean("requireMacdConfirmation"))
            requiredChecks.Add("macd-confirmation");

        return Consensus(family,
        [
            Check("three-swing-rsi-divergence", bullish, bearish,
                evidence.Pivots.Count == 3
                    ? $"{evidence.Reason} The three confirmed swing prices and RSI values are stored as decision evidence."
                    : "Three confirmed price swings do not form the required symmetric RSI divergence."),
            Check("five-minute-channel-proximity", bullish && evidence.IsNearFiveMinuteChannel,
                bearish && evidence.IsNearFiveMinuteChannel,
                $"The third confirmed swing must be within the outer {parameters.Decimal("channelProximityPercent")}% of its closed {channelPeriod}-candle 5-minute channel."),
            Check("one-hour-four-hour-context", contextAligned && bullish, contextAligned && bearish,
                $"At least {parameters.Int32("minimumAlignedContextTimeframes")} higher timeframe(s) must be positioned in the matching channel half. {contextRationale}"),
            Check("closed-reversal-confirmation", bullish && (!parameters.Boolean("requireClosedReversal") || evidence.HasReversalConfirmation),
                bearish && (!parameters.Boolean("requireClosedReversal") || evidence.HasReversalConfirmation),
                parameters.Boolean("requireClosedReversal")
                    ? "Wait for a closed 5-minute reversal candle to close beyond the prior candle's opposite extreme."
                    : "Closed reversal confirmation is optional in this approved parameter set."),
            Check("macd-confirmation", bullish && (!parameters.Boolean("requireMacdConfirmation") || evidence.HasMacdConfirmation),
                bearish && (!parameters.Boolean("requireMacdConfirmation") || evidence.HasMacdConfirmation),
                parameters.Boolean("requireMacdConfirmation")
                    ? "MACD line/signal ordering and histogram acceleration must confirm on closed 5-minute candles."
                    : "MACD confirmation is optional in this approved parameter set.")
        ], parameters.Int32("minimumAgreement"), requiredChecks);
    }

    private static ExperimentAnalysisResult Consensus(
        string family,
        IReadOnlyList<ExperimentSignalCheck> checks,
        int threshold = 4,
        IReadOnlyList<string>? requiredChecks = null) =>
        ExperimentAnalysisResult.FromConsensus(family, checks, threshold, requiredEntryChecks: requiredChecks);

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
        var all = new List<ExperimentCandleSeries>
        {
            input.Regime,
            input.Signal,
            input.Execution
        };
        if (input.HourlyContext is not null)
            all.Add(input.HourlyContext);
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
        if (all.Any(series => series.AsOfUtc > input.Execution.AsOfUtc))
        {
            reason = "higher-timeframe evidence cannot include a candle newer than the execution boundary";
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

    private static MacdValue Macd(IReadOnlyList<Candle> candles, int fast = 12, int slow = 26, int signal = 9) =>
        new MovingAverageConvergenceDivergenceCalculator(fast, slow, signal).Calculate(candles).Value
        ?? throw new InvalidOperationException("MACD warm-up was checked before calculation.");

    private static BollingerBandsValue Bands(IReadOnlyList<Candle> candles, int period, decimal multiplier) =>
        new BollingerBandsCalculator(period, multiplier).Calculate(candles).Value
        ?? throw new InvalidOperationException("Bollinger warm-up was checked before calculation.");

    private static decimal BandwidthPercent(IReadOnlyList<Candle> candles, int period, decimal deviation = 2m)
    {
        var bands = Bands(candles, period, deviation);
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

    internal static bool HasPersistentCompression(
        decimal[] bandwidthHistory, int persistenceCandles, decimal percentile, bool pastOnly)
    {
        ArgumentNullException.ThrowIfNull(bandwidthHistory);
        if (persistenceCandles <= 0 || bandwidthHistory.Length <= persistenceCandles)
            return false;
        return Enumerable.Range(bandwidthHistory.Length - persistenceCandles, persistenceCandles)
            .All(index => PercentileRank(
                pastOnly ? bandwidthHistory[..index] : bandwidthHistory,
                bandwidthHistory[index]) <= percentile);
    }

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
