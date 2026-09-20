using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Indicators;
using Trading.MarketData;
using Trading.Strategies.Approvals;

namespace Trading.Strategies;

/// <summary>Closed, bounded market states produced by the platform classifier.</summary>
public enum MarketRegime
{
    Unknown = 0,
    TrendingUp = 1,
    TrendingDown = 2,
    Ranging = 3,
    HighVolatility = 4
}

/// <summary>Immutable, bounded decimal parameters for the sole platform regime classifier.</summary>
public sealed class RegimeClassifierParameters
{
    public RegimeClassifierParameters(
        int fastEmaPeriod = 10,
        int slowEmaPeriod = 30,
        int atrPeriod = 14,
        int bollingerPeriod = 20,
        decimal bollingerStandardDeviationMultiplier = 2m,
        decimal trendEmaSeparationPercent = 0.50m,
        decimal highVolatilityAtrPercent = 3m,
        decimal highVolatilityBandWidthPercent = 8m,
        TimeSpan? maximumDataAge = null)
    {
        if (fastEmaPeriod is < 2 or > 100 || slowEmaPeriod is < 3 or > 250 || fastEmaPeriod >= slowEmaPeriod)
        {
            throw new ArgumentOutOfRangeException(nameof(fastEmaPeriod), "EMA periods must be bounded and fast must be less than slow.");
        }

        if (atrPeriod is < 2 or > 100 || bollingerPeriod is < 2 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(atrPeriod), "Indicator periods must be between 2 and 100.");
        }

        if (bollingerStandardDeviationMultiplier is < 0.5m or > 4m
            || trendEmaSeparationPercent is < 0.01m or > 20m
            || highVolatilityAtrPercent is < 0.01m or > 100m
            || highVolatilityBandWidthPercent is < 0.01m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(bollingerStandardDeviationMultiplier), "Classifier thresholds must be finite platform bounds.");
        }

        var age = maximumDataAge ?? TimeSpan.FromHours(2);
        if (age <= TimeSpan.Zero || age > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDataAge), "Maximum data age must be between zero and seven days.");
        }

        FastEmaPeriod = fastEmaPeriod;
        SlowEmaPeriod = slowEmaPeriod;
        AtrPeriod = atrPeriod;
        BollingerPeriod = bollingerPeriod;
        BollingerStandardDeviationMultiplier = bollingerStandardDeviationMultiplier;
        TrendEmaSeparationPercent = trendEmaSeparationPercent;
        HighVolatilityAtrPercent = highVolatilityAtrPercent;
        HighVolatilityBandWidthPercent = highVolatilityBandWidthPercent;
        MaximumDataAge = age;
    }

    public int FastEmaPeriod { get; }
    public int SlowEmaPeriod { get; }
    public int AtrPeriod { get; }
    public int BollingerPeriod { get; }
    public decimal BollingerStandardDeviationMultiplier { get; }
    public decimal TrendEmaSeparationPercent { get; }
    public decimal HighVolatilityAtrPercent { get; }
    public decimal HighVolatilityBandWidthPercent { get; }
    public TimeSpan MaximumDataAge { get; }
    public int RequiredCandleCount => Math.Max(SlowEmaPeriod, Math.Max(AtrPeriod, BollingerPeriod));
}

/// <summary>Immutable, explicit data and governance evidence for a regime observation.</summary>
public sealed class RegimeClassificationInput
{
    public RegimeClassificationInput(
        IReadOnlyList<Candle> candles,
        DateTimeOffset asOfUtc,
        RejectionGateEvaluation? rejectionGates)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (asOfUtc == default || asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The classifier as-of timestamp must be explicit UTC.", nameof(asOfUtc));
        }

        Candles = new ReadOnlyCollection<Candle>(candles.ToArray());
        AsOfUtc = asOfUtc;
        RejectionGates = rejectionGates;
    }

    public IReadOnlyList<Candle> Candles { get; }
    public DateTimeOffset AsOfUtc { get; }
    public RejectionGateEvaluation? RejectionGates { get; }
}

/// <summary>Explainable decimal-only evidence for one immutable classifier result.</summary>
public sealed class RegimeClassification
{
    internal RegimeClassification(
        MarketRegime regime,
        DateTimeOffset asOfUtc,
        RegimeClassifierParameters parameters,
        decimal? fastEma,
        decimal? slowEma,
        decimal? atrPercent,
        decimal? bandWidthPercent,
        string rationale)
    {
        ClassifierId = DeterministicRegimeClassifier.ClassifierId;
        ClassifierVersion = DeterministicRegimeClassifier.Version;
        ClassifierHash = DeterministicRegimeClassifier.ContentHash;
        Regime = regime;
        AsOfUtc = asOfUtc;
        Parameters = parameters;
        FastEma = fastEma;
        SlowEma = slowEma;
        AtrPercent = atrPercent;
        BandWidthPercent = bandWidthPercent;
        Rationale = rationale;
    }

    public string ClassifierId { get; }
    public int ClassifierVersion { get; }
    public string ClassifierHash { get; }
    public MarketRegime Regime { get; }
    public DateTimeOffset AsOfUtc { get; }
    public RegimeClassifierParameters Parameters { get; }
    public decimal? FastEma { get; }
    public decimal? SlowEma { get; }
    public decimal? AtrPercent { get; }
    public decimal? BandWidthPercent { get; }
    public string Rationale { get; }
}

/// <summary>
/// The only platform-selected regime classifier. It is pure and cannot create
/// intents, orders, sizing, or exchange requests.
/// </summary>
public sealed class DeterministicRegimeClassifier
{
    public const string ClassifierId = "platform.deterministic-regime";
    public const int Version = 1;
    public const string ContentHash = "78C6F7B0A49E5D6D81426FAEC5D62D6D46C465D3124798F7C0A2EDFCB5202CD1";

    private readonly RegimeClassifierParameters _parameters;

    public DeterministicRegimeClassifier(RegimeClassifierParameters? parameters = null) =>
        _parameters = parameters ?? new RegimeClassifierParameters();

    public RegimeClassifierParameters Parameters => _parameters;

    public RegimeClassification Evaluate(RegimeClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!HasAcceptedGatesAt(input.RejectionGates, input.AsOfUtc))
        {
            return Unknown(input, "Unavailable: rejection gates are missing, failed, or not aligned to the classifier as-of instant.");
        }

        if (!TryValidateCandles(input, out var reason))
        {
            return Unknown(input, reason);
        }

        if (input.Candles.Count < _parameters.RequiredCandleCount)
        {
            return Unknown(input, string.Create(CultureInfo.InvariantCulture, $"Unavailable: requires {_parameters.RequiredCandleCount} completed safe candles."));
        }

        var candles = input.Candles;
        var close = candles[^1].Close;
        if (close <= 0m)
        {
            return Unknown(input, "Unavailable: the latest completed close must be positive for percentage evidence.");
        }

        var fastEma = new ExponentialMovingAverageCalculator(_parameters.FastEmaPeriod).Calculate(candles).Value!.Value;
        var slowEma = new ExponentialMovingAverageCalculator(_parameters.SlowEmaPeriod).Calculate(candles).Value!.Value;
        var atr = new AverageTrueRangeCalculator(_parameters.AtrPeriod).Calculate(candles).Value!.Value;
        var bands = new BollingerBandsCalculator(_parameters.BollingerPeriod, _parameters.BollingerStandardDeviationMultiplier).Calculate(candles).Value!.Value;
        if (bands.Middle <= 0m)
        {
            return Unknown(input, "Unavailable: the completed Bollinger middle band must be positive for percentage evidence.");
        }

        var atrPercent = (atr / close) * 100m;
        var bandWidthPercent = ((bands.Upper - bands.Lower) / bands.Middle) * 100m;
        if (slowEma <= 0m)
        {
            return Unknown(input, "Unavailable: the completed slow EMA must be positive for percentage evidence.");
        }

        var separationPercent = (decimal.Abs(fastEma - slowEma) / slowEma) * 100m;
        if (atrPercent >= _parameters.HighVolatilityAtrPercent
            || bandWidthPercent >= _parameters.HighVolatilityBandWidthPercent)
        {
            return Result(MarketRegime.HighVolatility, input, fastEma, slowEma, atrPercent, bandWidthPercent,
                $"High volatility: ATR {atrPercent:F4}% / Bollinger width {bandWidthPercent:F4}% meets inclusive thresholds {_parameters.HighVolatilityAtrPercent:F4}% / {_parameters.HighVolatilityBandWidthPercent:F4}%.");
        }

        if (separationPercent >= _parameters.TrendEmaSeparationPercent && fastEma > slowEma && close >= fastEma)
        {
            return Result(MarketRegime.TrendingUp, input, fastEma, slowEma, atrPercent, bandWidthPercent,
                $"Trending up: EMA separation {separationPercent:F4}% meets inclusive {_parameters.TrendEmaSeparationPercent:F4}% threshold; close is at or above the fast EMA.");
        }

        if (separationPercent >= _parameters.TrendEmaSeparationPercent && fastEma < slowEma && close <= fastEma)
        {
            return Result(MarketRegime.TrendingDown, input, fastEma, slowEma, atrPercent, bandWidthPercent,
                $"Trending down: EMA separation {separationPercent:F4}% meets inclusive {_parameters.TrendEmaSeparationPercent:F4}% threshold; close is at or below the fast EMA.");
        }

        return Result(MarketRegime.Ranging, input, fastEma, slowEma, atrPercent, bandWidthPercent,
            $"Ranging: no inclusive high-volatility threshold or directional EMA condition was met; EMA separation {separationPercent:F4}%.");
    }

    private RegimeClassification Result(MarketRegime regime, RegimeClassificationInput input, decimal fastEma, decimal slowEma, decimal atrPercent, decimal bandWidthPercent, string rationale) =>
        new(regime, input.AsOfUtc, _parameters, fastEma, slowEma, atrPercent, bandWidthPercent, rationale);

    private RegimeClassification Unknown(RegimeClassificationInput input, string rationale) =>
        new(MarketRegime.Unknown, input.AsOfUtc, _parameters, null, null, null, null, rationale);

    private bool TryValidateCandles(RegimeClassificationInput input, out string reason)
    {
        if (input.Candles.Count == 0)
        {
            reason = "Unavailable: no completed candles were supplied.";
            return false;
        }

        var first = input.Candles[0];
        if (first is null)
        {
            reason = "Unavailable: candle evidence contains a null value.";
            return false;
        }

        var expectedDuration = TimeSpan.FromMinutes((int)first.Interval);
        if (first.Interval == Trading.Domain.Market.CandleInterval.None || !IsUtcAligned(first, expectedDuration))
        {
            reason = "Unavailable: candle interval or UTC boundary alignment is invalid.";
            return false;
        }

        for (var index = 0; index < input.Candles.Count; index++)
        {
            var candle = input.Candles[index];
            if (candle is null || !candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal || candle.IsDerived)
            {
                reason = "Unavailable: all evidence must be safe, source, completed candles.";
                return false;
            }

            if (candle.Interval != first.Interval || !string.Equals(candle.Symbol, first.Symbol, StringComparison.OrdinalIgnoreCase)
                || !IsUtcAligned(candle, expectedDuration))
            {
                reason = "Unavailable: candle symbol, interval, or UTC alignment is mixed or invalid.";
                return false;
            }

            if (candle.CloseTimeUtc > input.AsOfUtc)
            {
                reason = "Unavailable: future candle evidence is not permitted.";
                return false;
            }

            if (index > 0 && (candle.OpenTimeUtc != input.Candles[index - 1].CloseTimeUtc))
            {
                reason = "Unavailable: candle evidence is reversed, gapped, overlapping, or misaligned.";
                return false;
            }
        }

        if (input.AsOfUtc - input.Candles[^1].CloseTimeUtc > _parameters.MaximumDataAge)
        {
            reason = "Unavailable: the latest completed candle is stale at the classifier as-of instant.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsUtcAligned(Candle candle, TimeSpan expectedDuration) =>
        candle.CloseTimeUtc - candle.OpenTimeUtc == expectedDuration
        && candle.OpenTimeUtc.UtcTicks % expectedDuration.Ticks == 0
        && candle.CloseTimeUtc.UtcTicks % expectedDuration.Ticks == 0;

    private static bool HasAcceptedGatesAt(RejectionGateEvaluation? gates, DateTimeOffset asOfUtc) =>
        gates is { Accepted: true, Results.Count: > 0 }
        && gates.Results.All(result => result.Status == RejectionGateStatus.Passed && result.EvaluatedAtUtc == asOfUtc);
}
