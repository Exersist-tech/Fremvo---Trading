using System.Text.Json;

namespace Trading.Application.Experiments;

public sealed record ApprovedStrategyParameterField(
    string Key,
    string Label,
    string Type,
    string Description,
    decimal? Minimum = null,
    decimal? Maximum = null,
    decimal? Step = null,
    decimal? DefaultNumber = null,
    string? DefaultText = null,
    IReadOnlyList<string>? Options = null);

public sealed record ApprovedStrategyParameterDefinition(
    string StrategyId,
    string Name,
    string Description,
    IReadOnlyList<ApprovedStrategyParameterField> Fields,
    string DefaultsJson);

/// <summary>
/// Owner-editable values for approved rule templates. Bounds keep every option within the
/// platform-reviewed operating range; users cannot supply code or add evaluator keys.
/// </summary>
public static class ApprovedStrategyParameters
{
    private static readonly IReadOnlyDictionary<string, ApprovedStrategyParameterDefinition> s_catalog =
        CreateCatalog();

    public static IReadOnlyList<ApprovedStrategyParameterDefinition> Catalog =>
        s_catalog.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    public static IReadOnlyDictionary<string, string> Defaults =>
        s_catalog.ToDictionary(item => item.Key, item => item.Value.DefaultsJson, StringComparer.Ordinal);

    public static ApprovedStrategyParameterDefinition For(string strategyId) =>
        s_catalog.TryGetValue(strategyId, out var definition)
            ? definition
            : throw new ArgumentException("The strategy is not in the approved catalog.", nameof(strategyId));

    public static bool TryNormalize(string strategyId, string? json, out string normalized, out string error)
    {
        if (!s_catalog.TryGetValue(strategyId, out var definition))
        {
            normalized = "{}";
            error = "The strategy is not in the approved catalog.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            json = definition.DefaultsJson;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Strategy settings must be a JSON object.");

            var supplied = document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var fields = definition.Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);
            var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in definition.Fields)
            {
                if (!supplied.TryGetValue(field.Key, out var value))
                {
                    values[field.Key] = strategyId switch
                    {
                        "platform.relative-strength-pullback-rotation"
                            when supplied.Count > 0 && field.Key is "relativeRankingModel" or "relativePlanModel"
                            => field.Key == "relativeRankingModel" ? "legacyRanks" : "legacyAtrPlan",
                        "platform.donchian-breakout-ensemble"
                            when supplied.Count > 0 && field.Key == "donchianPlanModel"
                            => "legacyAtrPlan",
                        "platform.bollinger-mean-reversion"
                            when supplied.Count > 0 && field.Key == "bollingerPlanModel"
                            => "legacyAtrPlan",
                        "platform.rsi-pullback"
                            when supplied.Count > 0 && field.Key == "rsiPlanModel"
                            => "legacyAtrPlan",
                        "platform.ema-trend-continuation"
                            when supplied.Count > 0 && field.Key == "emaPlanModel"
                            => "legacyAtrPlan",
                        "platform.volatility-compression-breakout"
                            when supplied.Count > 0 && field.Key == "compressionPlanModel"
                            => "legacyAtrPlan",
                        "platform.macd-volume"
                            when supplied.Count > 0 && field.Key == "macdPlanModel"
                            => "legacyAtrPlan",
                        "platform.cross-sectional-momentum-rotation"
                            when supplied.Count > 0 && field.Key == "momentumPlanModel"
                            => "legacyAtrPlan",
                        "platform.three-swing-channel-divergence"
                            when supplied.Count > 0 && field.Key == "threeSwingPlanModel"
                            => "legacyAtrPlan",
                        "platform.regime-switching-ensemble"
                            when supplied.Count > 0 && field.Key == "regimePlanModel"
                            => "legacyAtrPlan",
                        _ => DefaultValue(field)
                    };
                    continue;
                }

                switch (field.Type)
                {
                    case "number":
                    {
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number)
                            || number < field.Minimum || number > field.Maximum
                            || field.Step is > 0m && (number - field.Minimum!.Value) % field.Step.Value != 0m)
                            throw new FormatException($"{field.Label} must be between {field.Minimum} and {field.Maximum} using step {field.Step}.");
                        values[field.Key] = number;
                        break;
                    }
                    case "integer":
                    {
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)
                            || number < field.Minimum || number > field.Maximum
                            || field.Step is > 0m && (number - field.Minimum!.Value) % field.Step.Value != 0m)
                            throw new FormatException($"{field.Label} must be a whole number between {field.Minimum} and {field.Maximum}.");
                        values[field.Key] = number;
                        break;
                    }
                    case "select":
                    {
                        if (value.ValueKind != JsonValueKind.String)
                            throw new FormatException($"{field.Label} must be selected from its approved options.");
                        var text = value.GetString();
                        if (text is null || field.Options?.Contains(text, StringComparer.Ordinal) != true)
                            throw new FormatException($"{field.Label} must be selected from its approved options.");
                        values[field.Key] = text;
                        break;
                    }
                    case "boolean":
                        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new FormatException($"{field.Label} must be enabled or disabled.");
                        values[field.Key] = value.GetBoolean();
                        break;
                    default:
                        throw new InvalidOperationException("An approved strategy parameter has an unsupported type.");
                }
            }

            var unknown = supplied.Keys.FirstOrDefault(key => !fields.ContainsKey(key));
            if (unknown is not null)
                throw new FormatException($"'{unknown}' is not an editable parameter for this strategy.");

            string? invalidRelationship = strategyId switch
            {
                "platform.ema-trend-continuation" =>
                    ReadInteger(values, "regimeFastEma") < ReadInteger(values, "regimeSlowEma")
                    && ReadInteger(values, "signalPullbackEma") < ReadInteger(values, "signalInvalidationEma")
                    && ReadNumber(values, "minimumAtrPercentile") < ReadNumber(values, "maximumAtrPercentile")
                        ? null : "Fast EMA periods and ATR percentile bounds must be strictly ordered.",
                "platform.donchian-breakout-ensemble" =>
                    ReadInteger(values, "shortChannel") < ReadInteger(values, "mediumChannel")
                    && ReadInteger(values, "mediumChannel") < ReadInteger(values, "longChannel")
                        ? null : "Donchian channel periods must be strictly increasing.",
                "platform.rsi-pullback" =>
                    ReadNumber(values, "longRsiMinimum") < ReadNumber(values, "longRsiMaximum")
                    && ReadNumber(values, "longRsiMaximum") < ReadNumber(values, "shortRsiMinimum")
                    && ReadNumber(values, "shortRsiMinimum") < ReadNumber(values, "shortRsiMaximum")
                    && ReadInteger(values, "regimeFastEma") < ReadInteger(values, "regimeSlowEma")
                        ? null : "RSI zones and regime EMA periods must be strictly ordered.",
                "platform.macd-volume" =>
                    ReadInteger(values, "macdFast") < ReadInteger(values, "macdSlow")
                    && ReadInteger(values, "regimeFastEma") < ReadInteger(values, "regimeSlowEma")
                        ? null : "MACD and regime fast periods must be below their slow periods.",
                "platform.volatility-compression-breakout" =>
                    ReadInteger(values, "regimeFastEma") < ReadInteger(values, "regimeSlowEma")
                        ? null : "The regime fast EMA period must be below its slow EMA period.",
                "platform.cross-sectional-momentum-rotation" =>
                    ReadInteger(values, "momentumRankLookback") < ReadInteger(values, "trendRankLookback")
                    && ReadNumber(values, "momentumMediumWeight") + ReadNumber(values, "momentumLongWeight")
                        + ReadNumber(values, "momentumTrendWeight") + ReadNumber(values, "momentumLiquidityWeight") == 1m
                        ? null : "Momentum lookbacks must be ordered and the four positive rank weights must sum to one.",
                "platform.relative-strength-pullback-rotation" =>
                    ReadInteger(values, "momentumRankLookback") < ReadInteger(values, "trendRankLookback")
                    && ReadInteger(values, "pullbackFastEma") < ReadInteger(values, "pullbackSlowEma")
                    && ReadInteger(values, "stopSwingLookback") <= ReadInteger(values, "targetChannelLookback")
                    && ReadNumber(values, "pullbackRsiMinimum") < ReadNumber(values, "pullbackRsiMaximum")
                    && ReadNumber(values, "relativeMediumWeight") + ReadNumber(values, "relativeLongWeight")
                        + ReadNumber(values, "relativeBenchmarkWeight") + ReadNumber(values, "relativeMedianWeight")
                        + ReadNumber(values, "relativeRiskAdjustedWeight") == 1m
                        ? null : "Relative-strength lookbacks, pullback bands, EMA periods, and rank weights must be ordered and valid.",
                "platform.session-conditioned-breakout" =>
                    new TimeSpan(ReadInteger(values, "sessionStartHour"), ReadInteger(values, "sessionStartMinute"), 0)
                    < new TimeSpan(ReadInteger(values, "sessionEndHour"), ReadInteger(values, "sessionEndMinute"), 0)
                        ? null : "The session end must be later than the session start on the same day.",
                "platform.three-swing-channel-divergence" =>
                    ReadInteger(values, "macdFast") < ReadInteger(values, "macdSlow")
                    && ReadNumber(values, "contextBullishMaximumPercent") < 50m
                    && ReadNumber(values, "contextBearishMinimumPercent") > 50m
                        ? null : "MACD periods must be ordered and higher-timeframe channel thresholds must remain on their matching channel halves.",
                _ => null
            };
            if (invalidRelationship is not null)
                throw new FormatException(invalidRelationship);

            normalized = JsonSerializer.Serialize(values);
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            normalized = "{}";
            error = "Strategy settings are not valid JSON.";
            return false;
        }
        catch (FormatException exception)
        {
            normalized = "{}";
            error = exception.Message;
            return false;
        }
    }

    public static StrategyParameterValues Read(string strategyId, string? json)
    {
        if (!TryNormalize(strategyId, json, out var normalized, out var error))
            throw new ArgumentException(error, nameof(json));
        using var document = JsonDocument.Parse(normalized);
        return new StrategyParameterValues(document.RootElement.Clone());
    }

    private static int ReadInteger(SortedDictionary<string, object> values, string key) =>
        Convert.ToInt32(values[key], System.Globalization.CultureInfo.InvariantCulture);

    private static decimal ReadNumber(SortedDictionary<string, object> values, string key) =>
        Convert.ToDecimal(values[key], System.Globalization.CultureInfo.InvariantCulture);

    private static object DefaultValue(ApprovedStrategyParameterField field) =>
        field.Type switch
        {
            "number" => field.DefaultNumber!.Value,
            "integer" => decimal.ToInt32(field.DefaultNumber!.Value),
            "select" => field.DefaultText!,
            "boolean" => field.DefaultText == "true",
            _ => throw new InvalidOperationException("An approved strategy parameter has an unsupported type.")
        };

    private static Dictionary<string, ApprovedStrategyParameterDefinition> CreateCatalog()
    {
        static ApprovedStrategyParameterField N(string key, string label, decimal min, decimal max, decimal step, decimal value, string description) =>
            new(key, label, "number", description, min, max, step, value);
        static ApprovedStrategyParameterField I(
            string key, string label, int min, int max, int value, string description, int step = 1) =>
            new(key, label, "integer", description, min, max, step, value);
        static ApprovedStrategyParameterField S(string key, string label, string value, string description, params string[] options) =>
            new(key, label, "select", description, DefaultText: value, Options: options);
        static ApprovedStrategyParameterField B(string key, string label, bool value, string description) =>
            new(key, label, "boolean", description, DefaultText: value ? "true" : "false");
        static ApprovedStrategyParameterDefinition D(string id, string name, string description, params ApprovedStrategyParameterField[] fields)
        {
            var defaults = fields.ToDictionary(field => field.Key, DefaultValue, StringComparer.Ordinal);
            return new(id, name, description, fields, JsonSerializer.Serialize(defaults));
        }

        var items = new[]
        {
            D("platform.ema-trend-continuation", "EMA trend continuation",
                "Higher-timeframe trend, pullback, closed-candle resumption and volume confirmation.",
                I("minimumAgreement", "Minimum confirmations", 4, 5, 4, "Required directional checks out of five; the named entry triggers must also pass."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 80, 40, "Time exit on the signal interval; platform cap is 80."),
                I("regimeFastEma", "Regime fast EMA", 10, 100, 50, "Fast higher-timeframe trend EMA."),
                I("regimeSlowEma", "Regime slow EMA", 50, 300, 200, "Slow higher-timeframe trend/invalidation EMA."),
                I("regimeSlopeLookback", "Regime slope lookback", 1, 30, 5, "Completed regime candles used to measure fast EMA slope."),
                I("signalPullbackEma", "Signal pullback EMA", 5, 100, 20, "Signal EMA used for pullback and resumption."),
                I("signalInvalidationEma", "Signal invalidation EMA", 10, 150, 50, "Signal EMA that bounds pullback depth."),
                I("volumePeriod", "Volume baseline candles", 5, 100, 20, "Prior completed candles in average volume."),
                I("atrPeriod", "ATR period", 5, 50, 14, "Signal ATR period for entry-quality percentile."),
                N("minimumAtrPercentile", "Minimum ATR percentile", 0m, 50m, 1m, 5m, "Minimum historical ATR percentile to accept."),
                N("maximumAtrPercentile", "Maximum ATR percentile", 50m, 100m, 1m, 100m, "Maximum historical ATR percentile to accept."),
                S("emaPlanModel", "EMA paper protection", "pullbackSwing",
                    "New paper positions freeze a stop below the closed pullback swing and an estimated risk-multiple target. Saved ATR plans need review before new admissions; open positions keep their protection.",
                    "pullbackSwing", "legacyAtrPlan"),
                I("stopSwingLookback", "Pullback swing candles", 3, 20, 5,
                    "Closed signal candles including the resumption whose lowest low defines paper protection."),
                N("stopBufferAtr", "Pullback stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Additional signal ATR distance below the frozen pullback low."),
                N("targetRiskMultiple", "Estimated target risk multiple", 1m, 4m, .1m, 1.5m,
                    "Estimated paper target frozen at this multiple of signal entry-to-stop distance."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Veto a new paper fill if the later closed one-minute reference price leaves less than this gross target-to-stop ratio; costs may reduce it.")),
            D("platform.donchian-breakout-ensemble", "Donchian breakout ensemble",
                "Multi-window breakout, trend confirmation, participation and extension veto.",
                I("minimumAgreement", "Minimum confirmations", 4, 5, 4, "Required directional checks out of five; the breakout and participation triggers must also pass."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 100, 60, "Time exit on the signal interval; platform cap is 100."),
                I("shortChannel", "Short channel candles", 5, 60, 20, "Prior closed candles for the short Donchian channel."),
                I("exitChannel", "Donchian exit channel candles", 5, 60, 20,
                    "Prior closed lows defining a new-position downside channel invalidation; older reserved versions retain 20."),
                I("mediumChannel", "Medium channel candles", 10, 120, 55, "Prior closed candles for the medium Donchian channel."),
                I("longChannel", "Long channel candles", 20, 200, 100, "Prior closed candles for the long Donchian channel."),
                I("regimeEma", "Regime EMA", 50, 300, 200, "Higher-timeframe trend filter."),
                I("volumePeriod", "Volume baseline candles", 5, 100, 20, "Prior completed candles in average volume."),
                N("volumeMultiplier", "Breakout volume multiple", 1m, 3m, .05m, 1.1m, "Current volume required relative to baseline."),
                I("atrPeriod", "ATR period", 5, 50, 14, "ATR period for breakout extension veto."),
                N("maximumExtensionAtr", "Maximum extension (ATR)", .1m, 5m, .1m, 1m, "Maximum close distance beyond breakout level."),
                S("donchianPlanModel", "Donchian paper protection", "priorBreakRange",
                    "Select the reviewed pre-break-range stop and risk-multiple target for new paper positions. Legacy saved settings need owner review; open positions retain their recorded ATR plan.",
                    "priorBreakRange", "legacyAtrPlan"),
                N("stopBufferAtr", "Range stop buffer (ATR)", .1m, 2m, .1m, .2m,
                    "Subtract this many completed signal ATRs below the prior channel low for the paper stop."),
                N("targetRiskMultiple", "Estimated target (gross risk multiple)", 1m, 4m, .1m, 1.5m,
                    "Paper target distance above the signal close, measured in opening stop distances; no guaranteed fill or net profit.")),
            D("platform.bollinger-mean-reversion", "Bollinger mean reversion",
                "Range regime, band extreme, RSI extreme and later closed-band re-entry.",
                I("minimumAgreement", "Minimum confirmations", 5, 5, 5, "All five checks are required."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 80, 40, "Time exit on the signal interval; platform cap is 80."),
                I("bollingerPeriod", "Bollinger period", 5, 100, 20, "Moving-average window for bands."),
                N("bollingerDeviation", "Bollinger deviation", .5m, 4m, .1m, 2m, "Standard-deviation multiplier."),
                I("rsiPeriod", "RSI period", 5, 50, 14, "RSI lookback."),
                N("oversoldRsi", "Oversold RSI", 10m, 45m, 1m, 30m, "Prior RSI level for bullish mean-reversion setup."),
                N("overboughtRsi", "Overbought RSI", 55m, 90m, 1m, 70m, "Prior RSI level for bearish mean-reversion setup."),
                I("adxPeriod", "ADX period", 5, 30, 14, "Regime trend-strength lookback."),
                N("maximumAdx", "Maximum ranging ADX", 10m, 40m, 1m, 25m, "Entry requires ADX below this level."),
                I("flatEmaPeriod", "Flat-regime EMA", 10, 200, 50, "EMA for the low-slope regime check."),
                I("slopeLookback", "EMA slope lookback", 1, 30, 5, "Completed regime candles for EMA slope."),
                N("maximumEmaSlopePercent", "Maximum EMA slope (%)", .1m, 5m, .1m, 1m, "Maximum absolute EMA slope for a ranging market."),
                S("bollingerPlanModel", "Bollinger paper protection", "excursionMidBand",
                    "New paper positions place a frozen buffered stop below the closed excursion and estimate a target at the signal middle band. Review previously saved ATR plans before new entries; old positions retain their protection.",
                    "excursionMidBand", "legacyAtrPlan"),
                I("atrPeriod", "Excursion-stop ATR period", 5, 50, 14,
                    "Closed signal candles used for the volatility buffer below the excursion low."),
                N("stopBufferAtr", "Excursion-stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Additional completed signal ATR distance below the prior excursion and re-entry low."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, .8m,
                    "Reject a new paper entry if the frozen middle-band target offers less than this gross reward-to-stop-distance ratio; costs can lower the result.")),
            D("platform.rsi-pullback", "RSI pullback",
                "Higher-timeframe trend, bounded RSI pullback/turn, price resumption and volume.",
                I("minimumAgreement", "Minimum confirmations", 4, 5, 4, "Required directional checks out of five; the pullback and resumption triggers must also pass."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 80, 40, "Time exit on the signal interval; platform cap is 80."),
                I("regimeFastEma", "Regime fast EMA", 10, 100, 50, "Fast higher-timeframe trend EMA."),
                I("regimeSlowEma", "Regime slow EMA", 50, 300, 200, "Slow trend and structural invalidation EMA."),
                I("signalEma", "Signal resumption EMA", 5, 100, 20, "Signal EMA for closed-price resumption."),
                I("rsiPeriod", "RSI period", 5, 50, 14, "RSI lookback."),
                N("longExitRsi", "Long-position RSI exit level", 65m, 90m, 1m, 70m,
                    "Close a new paper position when the configured signal RSI reaches this upper level; older reserved versions retain 70."),
                N("longRsiMinimum", "Long pullback RSI minimum", 10m, 50m, 1m, 30m, "Lower bound of the bullish pullback zone."),
                N("longRsiMaximum", "Long pullback RSI maximum", 20m, 60m, 1m, 45m, "Upper bound of the bullish pullback zone."),
                N("shortRsiMinimum", "Short pullback RSI minimum", 40m, 80m, 1m, 55m, "Lower bound of the bearish pullback zone."),
                N("shortRsiMaximum", "Short pullback RSI maximum", 50m, 90m, 1m, 70m, "Upper bound of the bearish pullback zone."),
                I("volumePeriod", "Volume baseline candles", 5, 100, 20, "Prior completed candles in average volume."),
                S("rsiPlanModel", "RSI paper protection", "pullbackSwing",
                    "New paper positions freeze a stop beyond the closed signal pullback, with an estimated risk-multiple target. Review saved ATR plans before new entries; open trades keep their existing protection.",
                    "pullbackSwing", "legacyAtrPlan"),
                I("stopSwingLookback", "Pullback swing candles", 3, 20, 5,
                    "Closed signal candles ending at the resumption whose lowest low defines the paper stop and range failure."),
                I("atrPeriod", "Pullback stop ATR period", 5, 50, 14,
                    "Closed signal candles used to buffer the structural stop."),
                N("stopBufferAtr", "Pullback stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Additional signal ATR distance below the frozen swing low."),
                N("targetRiskMultiple", "Estimated target risk multiple", 1m, 4m, .1m, 1.5m,
                    "Freeze the estimated paper target at this multiple of the signal entry-to-stop distance."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Veto new paper entry if the later closed one-minute price leaves less than this gross reward-to-stop-distance ratio; costs may worsen results.")),
            D("platform.macd-volume", "MACD volume acceleration",
                "Trend regime, MACD cross, histogram acceleration, average and volume participation.",
                I("minimumAgreement", "Minimum confirmations", 4, 5, 4, "Required directional checks out of five; the MACD cross and participation triggers must also pass."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 100, 50, "Time exit on the signal interval; platform cap is 100."),
                I("regimeFastEma", "Regime fast EMA", 10, 100, 50, "Fast higher-timeframe trend EMA."),
                I("regimeSlowEma", "Regime slow EMA", 50, 300, 200, "Slow higher-timeframe trend EMA."),
                I("macdFast", "MACD fast EMA", 5, 30, 12, "Fast MACD EMA."),
                I("macdSlow", "MACD slow EMA", 15, 60, 26, "Slow MACD EMA; must exceed fast."),
                I("macdSignal", "MACD signal EMA", 3, 20, 9, "Signal EMA applied to MACD."),
                I("signalEma", "Signal trend EMA", 10, 150, 50, "Signal price trend confirmation EMA."),
                I("volumePeriod", "Volume baseline candles", 5, 100, 20, "Prior completed candles in average volume."),
                N("volumeMultiplier", "Minimum volume multiple", .5m, 3m, .05m, 1m, "Current volume required relative to baseline."),
                S("macdPlanModel", "MACD paper protection", "crossSwing",
                    "New paper positions freeze a stop beyond the closed pre-cross swing, with an estimated risk-multiple target. Review existing ATR plans before new admissions; old trades keep their protection.",
                    "crossSwing", "legacyAtrPlan"),
                I("stopSwingLookback", "Pre-cross swing candles", 3, 20, 5,
                    "Closed signal candles including the crossing bar whose lowest low defines the paper stop."),
                I("atrPeriod", "MACD stop ATR period", 5, 50, 14,
                    "Signal ATR lookback used to buffer the structural stop."),
                N("stopBufferAtr", "MACD stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Additional signal ATR distance below the frozen swing low."),
                N("targetRiskMultiple", "Estimated target risk multiple", 1m, 4m, .1m, 1.5m,
                    "Paper target frozen at this multiple of signal entry-to-stop risk."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Reject later simulated paper entry if the remaining gross target distance is insufficient relative to stop distance; fees and spread are not measured.")),
            D("platform.volatility-compression-breakout", "Volatility compression breakout",
                "Low historical volatility, persistent compression, channel break, expansion and higher-timeframe trend.",
                I("minimumAgreement", "Minimum confirmations", 4, 5, 4, "Required directional checks out of five; compression and breakout triggers must also pass."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 100, 50, "Time exit on the signal interval; platform cap is 100."),
                I("bollingerPeriod", "Bollinger period", 5, 100, 20, "Bandwidth calculation window."),
                N("bollingerDeviation", "Bollinger deviation", .5m, 4m, .1m, 2m, "Standard-deviation multiplier for bandwidth."),
                I("atrPeriod", "ATR period", 5, 50, 14, "ATR lookback."),
                N("compressionPercentile", "Maximum compression percentile", 5m, 40m, 1m, 20m, "Maximum historical percentile for current bandwidth and ATR."),
                I("persistenceCandles", "Compression persistence candles", 2, 20, 5, "Completed prior candles that must remain compressed."),
                N("persistencePercentile", "Persistence percentile", 10m, 50m, 1m, 30m, "Maximum bandwidth percentile for each persistence candle."),
                I("channelPeriod", "Breakout channel candles", 5, 100, 20, "Prior closed candles defining the breakout boundary."),
                I("exitChannel", "Frozen breakout exit channel candles", 5, 100, 20,
                    "Before admitting a new paper position, use this many preceding signal candles for the breakout high; close on a later fall back below that frozen boundary."),
                I("volumePeriod", "Volume baseline candles", 5, 100, 20, "Prior completed candles in average volume."),
                N("volumeMultiplier", "Breakout volume multiple", 1m, 3m, .05m, 1.2m, "Current volume required relative to baseline."),
                I("regimeFastEma", "Regime fast EMA", 10, 100, 50, "Fast higher-timeframe trend EMA."),
                I("regimeSlowEma", "Regime slow EMA", 50, 300, 200, "Slow higher-timeframe trend EMA."),
                N("maximumExtensionAtr", "Maximum extension (ATR)", .1m, 5m, .1m, 1m, "Maximum close distance beyond breakout level."),
                S("compressionPlanModel", "Compression paper protection", "priorRange",
                    "Freeze a stop below the prior compressed channel with an estimated risk-multiple target. Existing ATR plans require owner review before new entries.",
                    "priorRange", "legacyAtrPlan"),
                N("stopBufferAtr", "Prior range stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Subtract this many closed signal ATRs below the prior channel low."),
                N("targetRiskMultiple", "Estimated target risk multiple", 1m, 4m, .1m, 1.5m,
                    "Freeze the paper target at this multiple of signal entry-to-stop distance."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Reject later simulated paper entry if remaining gross target distance relative to stop distance falls below this threshold; costs are not measured.")),
            D("platform.cross-sectional-momentum-rotation", "Cross-sectional momentum rotation",
                "Point-in-time universe rank, positive trend, liquidity, volatility and correlation controls.",
                I("minimumAgreement", "Minimum confirmations", 5, 5, 5, "All five cross-sectional risk gates are mandatory."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 60, 30, "Time exit on the signal interval; platform cap is 60."),
                I("momentumRankLookback", "Momentum rank lookback", 10, 120, 30, "Daily return window used in momentum ranking."),
                I("trendRankLookback", "Trend rank lookback", 30, 180, 90, "Long daily return window used in ranking."),
                I("volatilityLookback", "Volatility lookback", 10, 200, 90, "Daily returns used to estimate realized volatility and correlation."),
                I("liquidityLookback", "Liquidity lookback", 5, 60, 30, "Daily observations used for median quote-volume liquidity."),
                I("trendEma", "Daily trend EMA", 50, 300, 200, "Daily absolute-trend filter."),
                N("topRankPercent", "Top rank percentage", 5m, 50m, 1m, 20m, "Maximum ranking percentile eligible for entry."),
                N("minimumLiquidity", "Minimum median quote volume", 10000m, 100000000m, 10000m, 1000000m, "Minimum point-in-time median daily quote volume."),
                N("maximumVolatility", "Maximum daily volatility", .01m, .5m, .01m, .10m, "Maximum return standard deviation."),
                N("maximumCorrelation", "Maximum benchmark correlation", .1m, 1m, .01m, .90m, "Maximum absolute XBT/EUR correlation for non-benchmark assets."),
                N("momentumMediumWeight", "Medium-momentum rank weight", 0m, 1m, .05m, .40m, "Weight for medium-period momentum ranking."),
                N("momentumLongWeight", "Long-momentum rank weight", 0m, 1m, .05m, .25m, "Weight for long-period momentum ranking."),
                N("momentumTrendWeight", "Trend-strength rank weight", 0m, 1m, .05m, .20m, "Weight for distance above the configured trend EMA."),
                N("momentumLiquidityWeight", "Liquidity rank weight", 0m, 1m, .05m, .15m, "Weight for point-in-time median quote volume."),
                N("momentumVolatilityPenalty", "Volatility penalty", 0m, 1m, .05m, .10m, "Penalty applied to higher-volatility rank."),
                N("momentumTurnoverPenalty", "Momentum rank-turnover penalty", 0m, 1m, .05m, .10m, "Penalty applied when medium and long ranks diverge."),
                S("momentumPlanModel", "Momentum paper protection", "dailySwing",
                    "New paper positions freeze a stop beyond a prior closed daily swing and an estimated target; older ATR plans require owner review before new admissions.",
                    "dailySwing", "legacyAtrPlan"),
                I("stopSwingLookback", "Prior daily swing candles", 3, 30, 10,
                    "Completed daily candles before the rank decision whose lowest low defines the protective stop."),
                I("atrPeriod", "Daily swing ATR period", 5, 50, 14,
                    "Completed daily candles used for the stop buffer."),
                N("stopBufferAtr", "Daily stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Additional daily ATR distance beneath the prior swing low."),
                N("targetRiskMultiple", "Estimated target risk multiple", 1m, 4m, .1m, 1.5m,
                    "Gross paper target frozen at this multiple of the decision close-to-stop distance."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Reject the later simulated paper price if remaining gross target distance is inadequate relative to stop risk; costs remain unmeasured.")),
            D("platform.relative-strength-pullback-rotation", "Relative-strength pullback rotation",
                "Relative rank, daily trend, 4-hour EMA pullback/RSI recovery and 1-hour confirmation.",
                I("minimumAgreement", "Minimum confirmations", 5, 5, 5, "All five relative-strength risk gates are mandatory."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 240, 180, "Time exit on the signal interval; platform cap is 240."),
                I("trendRankLookback", "Daily trend lookback", 30, 180, 90, "Long daily return window in cross-sectional ranking."),
                I("momentumRankLookback", "Relative momentum lookback", 10, 120, 30, "Medium return window used for relative momentum ranking."),
                I("volatilityLookback", "Volatility lookback", 10, 200, 90, "Daily returns used to estimate realized volatility."),
                I("liquidityLookback", "Liquidity lookback", 5, 60, 30, "Daily observations used for median quote-volume liquidity."),
                I("trendEma", "Daily trend EMA", 50, 300, 200, "Daily absolute-trend filter."),
                N("topRankPercent", "Top rank percentage", 5m, 50m, 1m, 20m, "Maximum ranking percentile eligible for entry."),
                N("minimumLiquidity", "Minimum median quote volume", 10000m, 100000000m, 10000m, 1000000m, "Minimum point-in-time median daily quote volume."),
                N("maximumVolatility", "Maximum daily volatility", .01m, .5m, .01m, .10m, "Maximum return standard deviation."),
                I("pullbackFastEma", "4-hour pullback fast EMA", 5, 100, 20, "Fast EMA used to measure pullback distance."),
                I("pullbackSlowEma", "4-hour structural EMA", 10, 150, 50, "Slow EMA structural support."),
                I("atrPeriod", "4-hour ATR period", 5, 50, 14, "ATR period bounding pullback distance."),
                N("maximumPullbackAtr", "Maximum pullback distance (ATR)", .1m, 3m, .1m, 1m, "Maximum distance from configured EMA support."),
                I("rsiPeriod", "4-hour RSI period", 5, 50, 14, "RSI recovery lookback."),
                N("pullbackRsiMinimum", "Pullback RSI minimum", 10m, 50m, 1m, 35m, "Lower RSI bound for cooled pullback."),
                N("pullbackRsiMaximum", "Pullback RSI maximum", 40m, 70m, 1m, 55m, "Upper RSI bound for cooled pullback."),
                S("relativeRankingModel", "Relative ranking model", "dailyExcessBreadth",
                    "Daily excess breadth uses the share of closed daily returns beating XBT/EUR and the eligible-universe median. Legacy rankings require review before new paper entries.",
                    "dailyExcessBreadth", "legacyRanks"),
                S("relativePlanModel", "Relative paper protection", "fourHourStructure",
                    "A new position uses the confirmed 4-hour swing low, ATR buffer and prior channel high. Legacy ATR plans need owner review for new entries; open positions keep their stops.",
                    "fourHourStructure", "legacyAtrPlan"),
                I("stopSwingLookback", "4-hour protective swing candles", 3, 20, 5,
                    "Completed 4-hour setup candles whose lowest low defines structural invalidation."),
                N("stopBufferAtr", "4-hour stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Additional configured 4-hour ATR distance beneath the protective swing."),
                I("targetChannelLookback", "4-hour target channel candles", 5, 40, 20,
                    "Completed 4-hour candles whose prior high is the estimated resistance target."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Reject new paper entries when the prior-channel target offers less than this gross reward-to-stop-distance ratio; costs can lower realized reward."),
                N("relativeMediumWeight", "Medium relative-strength weight", 0m, 1m, .05m, 0m, "Weight for medium-period return relative to XBT/EUR."),
                N("relativeLongWeight", "Long relative-strength weight", 0m, 1m, .05m, .30m, "Weight for long-horizon absolute return rank."),
                N("relativeBenchmarkWeight", "Benchmark-relative weight", 0m, 1m, .05m, .25m, "Weight for the share of daily returns exceeding same-day XBT/EUR returns in the daily-excess model."),
                N("relativeMedianWeight", "Universe-median-relative weight", 0m, 1m, .05m, .20m, "Weight for the share of daily returns exceeding the eligible-universe same-day median in the daily-excess model."),
                N("relativeRiskAdjustedWeight", "Risk-adjusted return weight", 0m, 1m, .05m, .25m, "Weight for return adjusted by realized volatility.")),
            D("platform.session-conditioned-breakout", "Session-conditioned breakout",
                "Closed Donchian breakout gated by an approved time zone/session and participation.",
                I("minimumAgreement", "Minimum confirmations", 5, 5, 5, "All gates remain mandatory; cost and baseline evidence cannot be bypassed."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 100, 60, "Time exit on the signal interval; platform cap is 100."),
                I("channelPeriod", "Breakout channel candles", 5, 100, 20, "Prior closed candles defining the breakout boundary."),
                I("volumePeriod", "Volume baseline candles", 5, 100, 20, "Prior completed candles in average volume."),
                N("volumeMultiplier", "Minimum volume multiple", .5m, 3m, .05m, 1m, "Current volume required relative to baseline."),
                S("sessionTimeZone", "Session time zone", "America/New_York", "Approved IANA market-session time zone.", "UTC", "America/New_York", "Europe/London", "Europe/Berlin"),
                I("sessionStartHour", "Session start hour", 0, 23, 9, "Local session start hour."),
                I("sessionStartMinute", "Session start minute", 0, 55, 5, "Local session start minute in five-minute steps.", 5),
                I("sessionEndHour", "Session end hour", 0, 23, 16, "Local session end hour."),
                I("sessionEndMinute", "Session end minute", 0, 55, 0, "Local session end minute in five-minute steps.", 5),
                B("weekdaysOnly", "Weekdays only", true, "Restrict the session gate to Monday through Friday.")),
            D("platform.regime-switching-ensemble", "Regime-switching ensemble",
                "Daily trend, breadth, volatility and bandwidth select a platform-approved component strategy.",
                I("minimumAgreement", "Minimum confirmations", 5, 5, 5, "All component confirmations are mandatory."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 80, 50, "Time exit on the signal interval; platform cap is 80."),
                I("regimeFastEma", "Regime fast EMA", 10, 100, 50, "Fast trend EMA."),
                I("regimeSlowEma", "Regime slow EMA", 50, 300, 200, "Slow trend EMA."),
                I("slopeLookback", "EMA slope lookback", 1, 30, 5, "Completed regime candles used for EMA slope."),
                I("signalMomentumLookback", "Signal momentum lookback", 1, 50, 5, "Closed signal candles between the current close and medium-term momentum reference."),
                I("atrPeriod", "ATR period", 5, 50, 14, "Volatility-regime ATR lookback."),
                N("crisisAtrPercent", "Crisis ATR percent", 2m, 20m, .5m, 8m, "Block new exposure at or above this ATR percentage."),
                I("bollingerPeriod", "Bandwidth Bollinger period", 5, 100, 20, "Band-width calculation window."),
                N("bollingerDeviation", "Bollinger deviation", .5m, 4m, .1m, 2m, "Standard-deviation multiplier for regime bandwidth."),
                N("crisisBandwidthPercent", "Crisis bandwidth percent", 5m, 50m, 1m, 20m, "Block new exposure at or above this band-width percentage."),
                N("minimumBreadth", "Minimum breadth fraction", .05m, .8m, .05m, .20m, "Minimum eligible universe fraction above daily EMA trend."),
                N("compressionPercentile", "Compression regime percentile", .05m, .5m, .01m, .20m, "Bandwidth percentile that selects compression breakout."),
                N("expansionAtrPercent", "Expansion ATR percent", 1m, 10m, .5m, 4m, "At or above this ATR percentage, block late new entries."),
                I("momentumRankLookback", "Momentum rank lookback", 10, 120, 30, "Daily return window used to rank candidate markets."),
                I("trendRankLookback", "Trend rank lookback", 30, 180, 90, "Daily absolute-return window used as a trend gate."),
                I("trendEma", "Daily trend EMA", 50, 300, 200, "Daily EMA required for absolute trend confirmation."),
                S("regimePlanModel", "Ensemble paper protection", "fourHourSwing",
                    "An ensemble-specific prior closed four-hour swing stop and gross target; not the selected component's own stop. Old ATR-only plans require owner review for new entries.",
                    "fourHourSwing", "legacyAtrPlan"),
                I("stopSwingLookback", "Prior four-hour swing candles", 3, 20, 5,
                    "Completed four-hour signal candles preceding the component decision whose lowest low sets the ensemble stop."),
                I("planAtrPeriod", "Signal stop ATR period", 5, 50, 14,
                    "Closed four-hour ATR used for the stop buffer; distinct from the daily regime ATR."),
                N("stopBufferAtr", "Four-hour stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Subtract this many closed four-hour ATRs beneath the preceding swing low."),
                N("targetRiskMultiple", "Estimated target risk multiple", 1m, 4m, .1m, 1.5m,
                    "Freeze the ensemble's gross paper target at this multiple of signal entry-to-stop risk."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Reject a later simulated paper price when its remaining gross reward relative to the frozen stop is too low; costs are unmeasured.")),
            D("platform.three-swing-channel-divergence", "Three-swing channel divergence",
                "Three confirmed RSI-divergent pivots, channel location, higher-timeframe alignment, reversal and MACD.",
                I("minimumAgreement", "Minimum confirmations", 4, 5, 4, "Required checks out of five; pivots, channel and context remain mandatory, as do enabled reversal/MACD confirmations."),
                I("maximumHoldingCandles", "Maximum holding candles", 1, 80, 40, "Time exit on the signal interval; platform cap is 80."),
                I("rsiPeriod", "RSI period", 5, 50, 14, "RSI used at confirmed price pivots."),
                I("macdFast", "MACD fast EMA", 5, 30, 12, "Fast MACD EMA."),
                I("macdSlow", "MACD slow EMA", 15, 60, 26, "Slow MACD EMA; must exceed fast."),
                I("macdSignal", "MACD signal EMA", 3, 20, 9, "Signal EMA applied to MACD."),
                I("channelPeriod", "Channel lookback", 20, 100, 50, "Closed-candle high/low channel period."),
                I("pivotSideBars", "Pivot confirmation bars", 1, 5, 2, "Bars required on each side of a confirmed swing."),
                I("maximumPivotLookback", "Maximum pivot lookback", 30, 240, 120, "Recent closed candles searched for the last three pivots."),
                N("channelProximityPercent", "Signal-channel proximity (%)", 5m, 40m, 1m, 20m, "Distance from outer channel boundary for the third swing."),
                N("contextBearishMinimumPercent", "Bearish context minimum (%)", 50m, 90m, 1m, 60m, "Higher-timeframe channel location for bearish context."),
                N("contextBullishMaximumPercent", "Bullish context maximum (%)", 10m, 50m, 1m, 40m, "Higher-timeframe channel location for bullish context."),
                I("minimumAlignedContextTimeframes", "Minimum aligned context timeframes", 1, 2, 1, "Required aligned 1-hour/4-hour channel contexts."),
                N("minimumRsiDivergencePoints", "Minimum RSI divergence points", 0m, 10m, .1m, 0m, "Minimum RSI decline/rise between each consecutive confirmed swing."),
                N("minimumPriceProgressPercent", "Minimum three-swing price progress (%)", 0m, 10m, .1m, 0m, "Minimum percentage progress from the first to the third price swing."),
                B("requireClosedReversal", "Require closed reversal candle", true, "Require a closed reversal beyond the previous candle's opposite extreme."),
                B("requireMacdConfirmation", "Require MACD confirmation", true, "Require aligned MACD line/signal and histogram acceleration."),
                S("threeSwingPlanModel", "Three-swing paper protection", "confirmedPivot",
                    "New entries freeze a stop beneath the confirmed third swing and an estimated risk-multiple target. Historical ATR-only protection requires owner review.",
                    "confirmedPivot", "legacyAtrPlan"),
                I("atrPeriod", "Signal stop ATR period", 5, 50, 14,
                    "Closed 5-minute ATR used for a buffer beneath the last confirmed swing low."),
                N("stopBufferAtr", "Third-swing stop ATR buffer", .1m, 2m, .1m, .2m,
                    "Subtract this many closed signal ATRs below the confirmed swing or any later reversal low."),
                N("targetRiskMultiple", "Estimated target risk multiple", 1m, 4m, .1m, 1.5m,
                    "Freeze a gross paper target at this multiple of signal close-to-stop distance."),
                N("minimumRewardRisk", "Minimum estimated reward/risk", .5m, 2m, .1m, 1m,
                    "Reject a later simulated paper price if its remaining gross reward relative to the frozen stop is too low; costs remain unmeasured."),
                B("exitOnMacdReversal", "Exit on closed MACD reversal", true,
                    "For new positions, invalidate on a later closed 5-minute MACD line falling below its configured signal, independently of numeric stop/target."))
        };
        return items.ToDictionary(item => item.StrategyId, StringComparer.Ordinal);
    }
}

public sealed class StrategyParameterValues
{
    private readonly JsonElement _root;

    internal StrategyParameterValues(JsonElement root) => _root = root;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These accessors communicate the JSON value types.")]
    public int Int32(string key) => _root.GetProperty(key).GetInt32();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These accessors communicate the JSON value types.")]
    public decimal Decimal(string key) => _root.GetProperty(key).GetDecimal();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These accessors communicate the JSON value types.")]
    public string String(string key) => _root.GetProperty(key).GetString()
        ?? throw new InvalidOperationException($"Strategy parameter '{key}' is missing.");

    public bool Boolean(string key) => _root.GetProperty(key).GetBoolean();
}
