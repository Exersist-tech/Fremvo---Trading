using System.Security.Cryptography;
using System.Text;
using Trading.Indicators;
using Trading.MarketData;

namespace Trading.Application.Experiments;

public sealed record PaperRegimeComponentSelection(
    string FamilyId,
    int Version,
    string StrategyParameters,
    IReadOnlyList<string> UniverseSymbols,
    DateTimeOffset RegimeAsOfUtc,
    DateTimeOffset SignalAsOfUtc,
    Trading.Domain.Market.CandleInterval SignalInterval,
    string ComponentDecisionFingerprint)
{
    internal static string Fingerprint(ExperimentAnalysisResult result)
    {
        if (result.Consensus is null)
            throw new ArgumentException("A selected component must have consensus evidence.", nameof(result));
        var evidence = result.Consensus;
        var canonical = string.Join("|", evidence.Checks.Select(check =>
            $"{check.Id}:{(int)check.Direction}:{check.Rationale}"))
            + $"|{evidence.RequiredAgreement}|{string.Join(",", evidence.RequiredEntryChecks)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

internal sealed record PaperRegimeClassification(
    IReadOnlyList<string> EligibleFamilies,
    decimal Breadth,
    string? BlockReason = null,
    bool IsTrendDown = false);

internal static class PaperRegimeClassifier
{
    public static PaperRegimeClassification Classify(
        IReadOnlyList<CrossSectionalRankEvidence> ranked,
        IReadOnlyList<Candle> regime,
        StrategyParameterValues parameters)
    {
        if (ranked.Count == 0 || regime.Count < ApprovedConsensusStrategyProfiles.RequiredHistory)
            return new([], 0m, "Complete point-in-time breadth or daily regime evidence is unavailable.");

        var breadth = ranked.Count(item => item.AboveEma200) / (decimal)ranked.Count;
        var fastPeriod = parameters.Int32("regimeFastEma");
        var slowPeriod = parameters.Int32("regimeSlowEma");
        var fast = new ExponentialMovingAverageCalculator(fastPeriod).Calculate(regime).Value!.Value;
        var slow = new ExponentialMovingAverageCalculator(slowPeriod).Calculate(regime).Value!.Value;
        var priorFast = new ExponentialMovingAverageCalculator(fastPeriod)
            .Calculate(regime.Take(regime.Count - parameters.Int32("slopeLookback")).ToArray()).Value!.Value;
        var atr = new AverageTrueRangeCalculator(parameters.Int32("atrPeriod")).Calculate(regime).Value!.Value;
        var atrPercent = atr / regime[^1].Close * 100m;
        var bandPeriod = parameters.Int32("bollingerPeriod");
        var deviation = parameters.Decimal("bollingerDeviation");
        decimal Bandwidth(IReadOnlyList<Candle> candles)
        {
            var bands = new BollingerBandsCalculator(bandPeriod, deviation).Calculate(candles).Value!.Value;
            return bands.Middle <= 0m ? decimal.MaxValue : (bands.Upper - bands.Lower) / bands.Middle * 100m;
        }

        var bandwidth = Bandwidth(regime);
        var historyStart = Math.Max(100, bandPeriod);
        var bandwidthHistory = Enumerable.Range(historyStart, regime.Count - historyStart + 1)
            .Select(count => Bandwidth(regime.Take(count).ToArray()))
            .ToArray();
        var percentile = bandwidthHistory.Count(value => value <= bandwidth) / (decimal)bandwidthHistory.Length;
        if (atrPercent <= 0m || atrPercent >= parameters.Decimal("crisisAtrPercent")
            || bandwidth >= parameters.Decimal("crisisBandwidthPercent")
            || breadth < parameters.Decimal("minimumBreadth"))
            return new([], breadth, "CRISIS breadth or volatility permits no new Spot exposure.");
        if (fast < slow && fast < priorFast && regime[^1].Close < slow)
            return new([], breadth, "TREND_DOWN permits Spot reduction only.", IsTrendDown: true);
        if (percentile <= parameters.Decimal("compressionPercentile"))
            return new(["platform.volatility-compression-breakout"], breadth);
        if (fast > slow && fast > priorFast && regime[^1].Close > slow)
            return new(
                ["platform.ema-trend-continuation", "platform.donchian-breakout-ensemble", "platform.rsi-pullback"],
                breadth);
        if (atrPercent >= parameters.Decimal("expansionAtrPercent"))
            return new([], breadth, "EXPANSION manages existing positions but blocks late new entries.");
        return new(["platform.bollinger-mean-reversion"], breadth);
    }
}
