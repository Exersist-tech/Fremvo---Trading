using Trading.Domain.Market;
using Trading.Domain.Experiments;
using Trading.MarketData.Experiments;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Indicators;

namespace Trading.Application.Experiments;

/// <summary>
/// Supplies only the additional, platform-fixed evidence required by cross-sectional
/// paper research families. Implementations must read persisted evidence only.
/// </summary>
public interface ISupplementalExperimentEvidenceProvider
{
    Task<ExperimentAnalysisResult?> EvaluateAsync(
        Guid ownerId,
        string familyId,
        ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed default: supplemental families remain unavailable unless explicitly wired.</summary>
public sealed class UnconfiguredSupplementalExperimentEvidenceProvider : ISupplementalExperimentEvidenceProvider
{
    public Task<ExperimentAnalysisResult?> EvaluateAsync(Guid ownerId, string familyId, ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance, CancellationToken cancellationToken = default) =>
        Task.FromResult<ExperimentAnalysisResult?>(null);
}

/// <summary>
/// Fixed paper-only evidence provider. It has no user parameters, network, credential, order, or
/// allocation surface. Cross-sectional evaluations always retain all three required symbols.
/// </summary>
public sealed class PlatformSupplementalExperimentEvidenceProvider : ISupplementalExperimentEvidenceProvider
{
    private static readonly string[] Universe = ["XBT/EUR", "ETH/EUR", "SOL/EUR", "XRP/EUR", "TRX/EUR", "DOGE/EUR", "ADA/EUR"];
    private readonly IExperimentCandleSeriesSource _candles;
    private readonly IPaperTrainingActivationReader? _activations;

    public PlatformSupplementalExperimentEvidenceProvider(
        IExperimentCandleSeriesSource candles,
        IPaperTrainingActivationReader? activations = null)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _activations = activations;
    }

    public async Task<ExperimentAnalysisResult?> EvaluateAsync(
        Guid ownerId, string familyId, ExperimentCandleSeries primarySeries, ExperimentResearchProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primarySeries);
        ArgumentNullException.ThrowIfNull(provenance);
        if (familyId is not ("platform.cross-sectional-momentum-rotation"
            or "platform.relative-strength-pullback-rotation"))
        {
            return null;
        }

        if (!HasExactSafeSeries(primarySeries))
        {
            return ExperimentAnalysisResult.Blocked(
                "Supplemental evidence requires a safe chronological series closed exactly at the UTC as-of.");
        }
        return familyId switch
        {
            "platform.cross-sectional-momentum-rotation" => await CrossAsync(ownerId, primarySeries, provenance, false, cancellationToken).ConfigureAwait(false),
            "platform.relative-strength-pullback-rotation" => await CrossAsync(ownerId, primarySeries, provenance, true, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

    private async Task<ExperimentAnalysisResult> CrossAsync(Guid ownerId, ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance, bool relativeStrength, CancellationToken cancellationToken)
    {
        const int history = ApprovedConsensusStrategyProfiles.RequiredHistory;
        var symbols = (await UniverseSymbolsAsync(ownerId, cancellationToken).ConfigureAwait(false))
            .Append(primarySeries.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var universe = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            var result = await _candles.GetClosedSeriesAsync(
                new ExperimentCandleSeriesRequest(
                    symbol,
                    CandleInterval.OneDay,
                    AlignDown(primarySeries.AsOfUtc, TimeSpan.FromDays(1)),
                    history),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsAvailable || result.Series is null || result.Series.AsOfUtc != primarySeries.AsOfUtc
                && result.Series?.AsOfUtc != AlignDown(primarySeries.AsOfUtc, TimeSpan.FromDays(1))
                || result.Series?.Candles.Count != history)
            {
                return ExperimentAnalysisResult.Blocked(
                    $"Required fixed universe member '{symbol}' is unavailable, incomplete, or not closed exactly at the UTC as-of.");
            }
            universe.Add(symbol, result.Series.Candles);
        }

        var ranked = CrossSectionalConsensusRanking.Evaluate(universe)
            .OrderByDescending(item => relativeStrength ? item.RelativeStrengthScore : item.MomentumScore)
            .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ranked.Length != universe.Count)
            return ExperimentAnalysisResult.Blocked("Complete point-in-time ranking evidence is unavailable.");
        var selected = ranked.Single(item =>
            item.Symbol.Equals(primarySeries.Symbol, StringComparison.OrdinalIgnoreCase));
        var top = Array.IndexOf(ranked, selected) < Math.Max(1, (int)Math.Ceiling(ranked.Length * .20m));
        var correlationSafe = selected.Symbol.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase)
            || decimal.Abs(selected.CorrelationToBenchmark) <= .90m;

        if (!relativeStrength)
        {
            if (!top || selected.LongReturn <= 0m || !selected.AboveEma200
                || selected.MedianQuoteVolume < 1_000_000m
                || selected.Volatility > .10m || !correlationSafe)
            {
                return BearishExit(
                    "platform.cross-sectional-momentum-rotation",
                    "Buffered rank, absolute trend, liquidity, volatility, or correlation evidence requires a Spot exit.");
            }
            return ExperimentAnalysisResult.FromConsensus(
                "platform.cross-sectional-momentum-rotation",
                [
                    Check("top-momentum-percentile", top, "Instrument ranks in the top 20% of the complete point-in-time universe."),
                    Check("positive-absolute-return", selected.LongReturn > 0m, "Ninety-day absolute return is positive."),
                    Check("daily-long-term-trend", selected.AboveEma200, "Daily close is above EMA 200."),
                    Check("liquidity-and-history", selected.MedianQuoteVolume >= 1_000_000m, "Daily history and quote-volume evidence pass."),
                    Check("volatility-and-correlation", selected.Volatility <= .10m && correlationSafe, "Volatility and XBT-correlation limits pass.")
                ],
                5);
        }

        if (primarySeries.Interval != CandleInterval.FourHours || primarySeries.Candles.Count < 51)
            return ExperimentAnalysisResult.Blocked(
                "Relative-strength pullback requires complete four-hour setup evidence.");
        var executionResult = await _candles.GetClosedSeriesAsync(
            new ExperimentCandleSeriesRequest(
                primarySeries.Symbol,
                CandleInterval.OneHour,
                primarySeries.AsOfUtc,
                history),
            cancellationToken).ConfigureAwait(false);
        if (!executionResult.IsAvailable || executionResult.Series is null
            || executionResult.Series.Candles.Count < 2)
            return ExperimentAnalysisResult.Blocked(
                "Relative-strength pullback requires complete one-hour execution evidence.");
        var setup = primarySeries.Candles;
        var execution = executionResult.Series.Candles;
        var ema20 = new ExponentialMovingAverageCalculator(20).Calculate(setup).Value!.Value;
        var ema50 = new ExponentialMovingAverageCalculator(50).Calculate(setup).Value!.Value;
        var atr = new AverageTrueRangeCalculator(14).Calculate(setup).Value!.Value;
        var rsi = new RelativeStrengthIndexCalculator(14).Calculate(setup).Value!.Value;
        var priorRsi = new RelativeStrengthIndexCalculator(14)
            .Calculate(setup.Take(setup.Count - 1).ToArray()).Value!.Value;
        var distance = Math.Min(decimal.Abs(setup[^1].Close - ema20), decimal.Abs(setup[^1].Close - ema50));
        if (!top || selected.LongReturn <= 0m || !selected.AboveEma200 || setup[^1].Close < ema50)
        {
            return BearishExit(
                "platform.relative-strength-pullback-rotation",
                "Relative-strength rank, daily trend, or four-hour structure invalidation requires a Spot exit.");
        }
        return ExperimentAnalysisResult.FromConsensus(
            "platform.relative-strength-pullback-rotation",
            [
                Check("top-relative-strength", top, "Asset remains in the top 20% relative-strength group."),
                Check("positive-daily-trend", selected.LongReturn > 0m && selected.AboveEma200, "Daily absolute trend is positive."),
                Check("bounded-four-hour-pullback", distance <= atr && setup[^1].Close >= ema50, "Four-hour pullback is within 1 ATR of EMA 20 or 50 without structural failure."),
                Check("four-hour-rsi-cooled", rsi is >= 35m and <= 55m && rsi >= priorRsi, "Four-hour RSI has cooled and stopped deteriorating."),
                Check("one-hour-confirmation", execution[^1].Close > execution[^2].High, "A closed one-hour candle confirms renewed upside.")
            ],
            5);
    }

    private async Task<IReadOnlyList<string>> UniverseSymbolsAsync(
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        if (_activations is null || ownerId == Guid.Empty)
            return Universe;
        var activation = await _activations.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        var scan = activation?.QualificationResults
            .LastOrDefault(result => result.StrategyId == "platform.scanner");
        if (scan is null)
            return Universe;
        var symbols = activation!.QualificationResults
            .Where(result => result.StrategyId == "platform.scanner-universe"
                && result.Reason.StartsWith(scan.DatasetFingerprint, StringComparison.Ordinal))
            .Select(result => result.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return symbols.Length == 0 ? Universe : symbols;
    }

    private static ExperimentSignalCheck Check(string id, bool bullish, string rationale) =>
        new(id, bullish ? ExperimentSignalDirection.Bullish : ExperimentSignalDirection.Neutral, rationale);

    private static ExperimentAnalysisResult BearishExit(string familyId, string rationale) =>
        ExperimentAnalysisResult.FromConsensus(
            familyId,
            Enumerable.Range(1, 5)
                .Select(index => new ExperimentSignalCheck(
                    $"mandatory-exit-{index}",
                    ExperimentSignalDirection.Bearish,
                    rationale))
                .ToArray(),
            5);

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - value.UtcTicks % interval.Ticks, TimeSpan.Zero);

    private static bool HasExactSafeSeries(ExperimentCandleSeries series)
    {
        if (series.Candles.Count == 0 || series.Candles[^1].CloseTimeUtc != series.AsOfUtc)
            return false;
        Candle? previous = null;
        foreach (var candle in series.Candles)
        {
            if (candle is null || !candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal
                || candle.CloseTimeUtc > series.AsOfUtc
                || (previous is not null && (candle.OpenTimeUtc <= previous.OpenTimeUtc || candle.CloseTimeUtc <= previous.CloseTimeUtc)))
                return false;
            previous = candle;
        }
        return true;
    }
}
