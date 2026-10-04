using Trading.Domain.Market;
using Trading.Domain.Experiments;
using Trading.MarketData.Experiments;
using Trading.MarketData;
using Trading.Strategies;
using Trading.Indicators;
using Trading.Strategies.Approvals;

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
        string strategyParameters = "{}",
        ExperimentCandleSeries? regimeSeries = null,
        ExperimentCandleSeries? executionSeries = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed default: supplemental families remain unavailable unless explicitly wired.</summary>
public sealed class UnconfiguredSupplementalExperimentEvidenceProvider : ISupplementalExperimentEvidenceProvider
{
    public Task<ExperimentAnalysisResult?> EvaluateAsync(Guid ownerId, string familyId, ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance, string strategyParameters = "{}",
        ExperimentCandleSeries? regimeSeries = null, ExperimentCandleSeries? executionSeries = null,
        CancellationToken cancellationToken = default) =>
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
    private readonly ApprovedExperimentStrategyRegistry _strategies;

    public PlatformSupplementalExperimentEvidenceProvider(
        IExperimentCandleSeriesSource candles,
        IPaperTrainingActivationReader? activations = null,
        ApprovedExperimentStrategyRegistry? strategies = null)
    {
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
        _activations = activations;
        _strategies = strategies ?? ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
    }

    public async Task<ExperimentAnalysisResult?> EvaluateAsync(
        Guid ownerId, string familyId, ExperimentCandleSeries primarySeries, ExperimentResearchProvenance provenance,
        string strategyParameters = "{}",
        ExperimentCandleSeries? regimeSeries = null,
        ExperimentCandleSeries? executionSeries = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primarySeries);
        ArgumentNullException.ThrowIfNull(provenance);
        if (familyId is not ("platform.cross-sectional-momentum-rotation"
            or "platform.relative-strength-pullback-rotation"
            or "platform.regime-switching-ensemble"))
        {
            return null;
        }

        if (!ApprovedStrategyParameters.TryNormalize(familyId, strategyParameters, out _, out var parameterError))
            return ExperimentAnalysisResult.Blocked(
                $"{familyId}: saved strategy settings require review: {parameterError}");
        if (!HasExactSafeSeries(primarySeries))
        {
            return ExperimentAnalysisResult.Blocked(
                "Supplemental evidence requires a safe chronological series closed exactly at the UTC as-of.");
        }
        if (familyId == "platform.regime-switching-ensemble")
            return provenance.Approval.StrategyVersion.Identity.Version < 4
                ? null
                : await RegimeAsync(primarySeries, regimeSeries, provenance, strategyParameters, cancellationToken)
                    .ConfigureAwait(false);
        return familyId switch
        {
            "platform.cross-sectional-momentum-rotation" => await CrossAsync(ownerId, primarySeries, provenance, strategyParameters, false, executionSeries, cancellationToken).ConfigureAwait(false),
            "platform.relative-strength-pullback-rotation" => await CrossAsync(ownerId, primarySeries, provenance, strategyParameters, true, executionSeries, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

    private async Task<ExperimentAnalysisResult> RegimeAsync(
        ExperimentCandleSeries signal,
        ExperimentCandleSeries? regime,
        ExperimentResearchProvenance provenance,
        string parametersJson,
        CancellationToken cancellationToken)
    {
        const string family = "platform.regime-switching-ensemble";
        var selected = provenance.SelectedComponent;
        if (selected is null || regime is null || !HasExactSafeSeries(regime)
            || string.IsNullOrWhiteSpace(selected.FamilyId) || selected.Version <= 0
            || string.IsNullOrWhiteSpace(selected.StrategyParameters)
            || selected.RegimeAsOfUtc != regime.AsOfUtc
            || selected.SignalAsOfUtc != signal.AsOfUtc
            || selected.UniverseSymbols is not { Count: > 0 and <= 50 }
            || !selected.UniverseSymbols.Contains("XBT/EUR", StringComparer.OrdinalIgnoreCase)
            || !selected.UniverseSymbols.Contains(signal.Symbol, StringComparer.OrdinalIgnoreCase)
            || selected.UniverseSymbols.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != selected.UniverseSymbols.Count
            || selected.UniverseSymbols.Any(symbol => string.IsNullOrWhiteSpace(symbol)
                || !symbol.EndsWith("/EUR", StringComparison.OrdinalIgnoreCase)))
            return ExperimentAnalysisResult.Blocked(
                "Pinned component identity, exact closed regime boundary, or eligible-universe snapshot is unavailable.");

        var parameters = ApprovedStrategyParameters.Read(family, parametersJson);
        const int history = ApprovedConsensusStrategyProfiles.RequiredHistory;
        var universe = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in selected.UniverseSymbols)
        {
            var result = await _candles.GetClosedSeriesAsync(
                new ExperimentCandleSeriesRequest(symbol, CandleInterval.OneDay, selected.RegimeAsOfUtc, history),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsAvailable || result.Series is null || result.Series.AsOfUtc != selected.RegimeAsOfUtc
                || result.Series.Candles.Count != history || !HasExactSafeSeries(result.Series))
                return ExperimentAnalysisResult.Blocked("Pinned universe member lacks complete closed daily history.");
            universe.Add(symbol, result.Series.Candles);
        }
        var ranked = CrossSectionalConsensusRanking.Evaluate(universe,
            parameters.Int32("momentumRankLookback"),
            parameters.Int32("trendRankLookback"),
            parameters.Int32("trendEma"));
        if (ranked.Length != universe.Count)
            return ExperimentAnalysisResult.Blocked("Pinned eligible-universe ranking cannot be reproduced.");
        var classification = PaperRegimeClassifier.Classify(ranked, regime.Candles, parameters);
        if (classification.BlockReason is not null)
            return ExperimentAnalysisResult.Blocked(classification.BlockReason);
        if (!classification.EligibleFamilies.Contains(selected.FamilyId, StringComparer.Ordinal)
            || !_strategies.TryResolve(
                new StrategyTemplateVersionIdentity(selected.FamilyId, selected.Version), out var evaluator)
            || evaluator is null)
            return ExperimentAnalysisResult.Blocked("Pinned component is not approved for this closed market regime.");
        var profile = ApprovedConsensusStrategyProfiles.For(selected.FamilyId)
            .FirstOrDefault(item => item.Signal == selected.SignalInterval);
        if (profile is null)
            return ExperimentAnalysisResult.Blocked("Pinned component has no approved timeframe profile.");
        var intervals = new[] { profile.Regime, profile.Signal, profile.Execution }.Distinct();
        var series = new Dictionary<CandleInterval, ExperimentCandleSeries>();
        foreach (var interval in intervals)
        {
            var asOf = AlignDown(signal.AsOfUtc, TimeSpan.FromMinutes((int)interval));
            var result = await _candles.GetClosedSeriesAsync(
                new ExperimentCandleSeriesRequest(signal.Symbol, interval, asOf, history),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsAvailable || result.Series is null || result.Series.AsOfUtc != asOf
                || result.Series.Candles.Count != history || !HasExactSafeSeries(result.Series))
                return ExperimentAnalysisResult.Blocked("Pinned component lacks staged closed timeframe evidence.");
            series.Add(interval, result.Series);
        }
        var component = _strategies.EvaluateProfile(selected.FamilyId,
            series[profile.Regime], series[profile.Signal], series[profile.Execution],
            selected.StrategyParameters, strategyVersion: selected.Version);
        if (component.Outcome != ExperimentAnalysisOutcome.Analyzed || component.Value is not > 0m
            || component.Consensus is null
            || !PaperRegimeComponentSelection.Fingerprint(component).Equals(
                selected.ComponentDecisionFingerprint, StringComparison.Ordinal))
            return ExperimentAnalysisResult.Blocked(
                "Pinned component no longer reproduces its scanner admission decision.");

        return ExperimentAnalysisResult.FromConsensus(family,
                component.Consensus.Checks.Select(check => check with
                {
                    Rationale = $"{selected.FamilyId} v{selected.Version}: {check.Rationale} Complete eligible-universe breadth is {classification.Breadth:P2}."
                }).ToArray(),
                component.Consensus.RequiredAgreement,
                requiredEntryChecks: component.Consensus.RequiredEntryChecks)
            .WithSelectedComponent(selected);
    }

    private async Task<ExperimentAnalysisResult> CrossAsync(Guid ownerId, ExperimentCandleSeries primarySeries,
        ExperimentResearchProvenance provenance, string strategyParameters, bool relativeStrength,
        ExperimentCandleSeries? executionSeries, CancellationToken cancellationToken)
    {
        var family = relativeStrength
            ? "platform.relative-strength-pullback-rotation"
            : "platform.cross-sectional-momentum-rotation";
        var parameters = ApprovedStrategyParameters.Read(family, strategyParameters);
        var strategyVersion = provenance.Approval.StrategyVersion.Identity.Version;
        var revised = relativeStrength && strategyVersion >= 4;
        var pinnedMomentum = !relativeStrength && strategyVersion >= 4;
        if (revised && parameters.String("relativeRankingModel") != "dailyExcessBreadth")
            return ExperimentAnalysisResult.Blocked(
                "Legacy relative-strength ranking settings need owner review before new paper admissions.");
        if (pinnedMomentum && parameters.String("momentumPlanModel") != "dailySwing")
            return ExperimentAnalysisResult.Blocked(
                "Legacy momentum protection settings need owner review before new paper admissions.");
        if (provenance.Approval.StrategyVersion.Identity.Version >= 5
            && parameters.String("relativePlanModel") != "fourHourStructure")
            return ExperimentAnalysisResult.Blocked(
                "Legacy relative-strength protection settings need owner review before new paper admissions.");
        const int history = ApprovedConsensusStrategyProfiles.RequiredHistory;
        if ((revised || pinnedMomentum) && provenance.RankingUniverseSymbols is null)
            return ExperimentAnalysisResult.Blocked(
                "Versioned rotation analysis requires its pinned eligible-universe snapshot.");
        var symbols = provenance.RankingUniverseSymbols?.ToArray()
            ?? (await UniverseSymbolsAsync(ownerId, cancellationToken).ConfigureAwait(false))
                .Append(primarySeries.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (symbols.Length is < 2 or > 50 || !symbols.Contains(primarySeries.Symbol, StringComparer.OrdinalIgnoreCase)
            || !symbols.Contains("XBT/EUR", StringComparer.OrdinalIgnoreCase)
            || symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != symbols.Length
            || symbols.Any(symbol => string.IsNullOrWhiteSpace(symbol)
                || !symbol.EndsWith("/EUR", StringComparison.OrdinalIgnoreCase)))
            return ExperimentAnalysisResult.Blocked(
                "The ranked universe must contain unique same-quote markets and the XBT/EUR benchmark.");
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
            if (!result.IsAvailable || result.Series is null
                || result.Series.Candles.Count != history
                || !HasExactSafeSeries(result.Series)
                || (revised || pinnedMomentum
                    ? result.Series.AsOfUtc != AlignDown(primarySeries.AsOfUtc, TimeSpan.FromDays(1))
                    : result.Series.AsOfUtc != primarySeries.AsOfUtc
                        && result.Series.AsOfUtc != AlignDown(primarySeries.AsOfUtc, TimeSpan.FromDays(1))))
            {
                return ExperimentAnalysisResult.Blocked(
                    $"Required fixed universe member '{symbol}' is unavailable, incomplete, or not closed exactly at the UTC as-of.");
            }
            universe.Add(symbol, result.Series.Candles);
        }

        var ranked = CrossSectionalConsensusRanking.Evaluate(
                universe,
                parameters.Int32("momentumRankLookback"),
                parameters.Int32("trendRankLookback"),
                parameters.Int32("trendEma"),
                parameters.Int32("volatilityLookback"),
                parameters.Int32("liquidityLookback"),
                CrossSectionalConsensusRanking.ReadWeights(parameters, relativeStrength),
                legacyRanking: provenance.Approval.StrategyVersion.Identity.Version == 2,
                dailyExcessBreadth: revised)
            .OrderByDescending(item => relativeStrength ? item.RelativeStrengthScore : item.MomentumScore)
            .ThenBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ranked.Length != universe.Count)
            return ExperimentAnalysisResult.Blocked("Complete point-in-time ranking evidence is unavailable.");
        var selected = ranked.Single(item =>
            item.Symbol.Equals(primarySeries.Symbol, StringComparison.OrdinalIgnoreCase));
        var top = Array.IndexOf(ranked, selected) < Math.Max(1,
            (int)Math.Ceiling(ranked.Length * parameters.Decimal("topRankPercent") / 100m));
        var correlationSafe = relativeStrength
            || selected.Symbol.Equals("XBT/EUR", StringComparison.OrdinalIgnoreCase)
            || decimal.Abs(selected.CorrelationToBenchmark) <= parameters.Decimal("maximumCorrelation");

        if (!relativeStrength)
        {
            if (!top || selected.LongReturn <= 0m || revised && selected.BenchmarkExcessReturn <= 0m || !selected.AboveEma200
                || selected.MedianQuoteVolume < parameters.Decimal("minimumLiquidity")
                || selected.Volatility > parameters.Decimal("maximumVolatility") || !correlationSafe)
            {
                return BearishExit(
                    "platform.cross-sectional-momentum-rotation",
                    "Buffered rank, absolute trend, liquidity, volatility, or correlation evidence requires a Spot exit.");
            }
            return ExperimentAnalysisResult.FromConsensus(
                "platform.cross-sectional-momentum-rotation",
                [
                    Check("top-momentum-percentile", top, $"Instrument ranks in the top {parameters.Decimal("topRankPercent")}% of the complete point-in-time universe."),
                    Check("positive-absolute-return", selected.LongReturn > 0m, $"Configured {parameters.Int32("trendRankLookback")}-day absolute return is positive."),
                    Check("daily-long-term-trend", selected.AboveEma200, $"Daily close is above EMA {parameters.Int32("trendEma")}."),
                    Check("liquidity-and-history", selected.MedianQuoteVolume >= parameters.Decimal("minimumLiquidity"), "Daily history and configured quote-volume evidence pass."),
                    Check("volatility-and-correlation", selected.Volatility <= parameters.Decimal("maximumVolatility") && correlationSafe, "Configured volatility and XBT-correlation limits pass.")
                ],
                5);
        }

        var setupHistory = new[]
        {
            parameters.Int32("pullbackFastEma") + 1,
            parameters.Int32("pullbackSlowEma") + 1,
            parameters.Int32("atrPeriod") + 1,
            parameters.Int32("rsiPeriod") + 1
        }.Max();
        if (primarySeries.Interval != CandleInterval.FourHours || primarySeries.Candles.Count < setupHistory)
            return ExperimentAnalysisResult.Blocked(
                "Relative-strength pullback requires complete four-hour setup evidence.");
        var laterConfirmation = provenance.Approval.StrategyVersion.Identity.Version >= 5;
        if (laterConfirmation
            && (provenance.AdmissionCloseUtc is not DateTimeOffset pinnedClose
                || pinnedClose.Offset != TimeSpan.Zero
                || executionSeries?.AsOfUtc != pinnedClose))
            return ExperimentAnalysisResult.Blocked(
                "The later one-hour confirmation does not match the scanner's pinned admission.");
        var execution = laterConfirmation
            ? executionSeries
            : (await _candles.GetClosedSeriesAsync(
                new ExperimentCandleSeriesRequest(
                    primarySeries.Symbol,
                    CandleInterval.OneHour,
                    primarySeries.AsOfUtc,
                    history),
                cancellationToken).ConfigureAwait(false)).Series;
        if (execution is null || execution.Interval != CandleInterval.OneHour
            || !string.Equals(execution.Symbol, primarySeries.Symbol, StringComparison.OrdinalIgnoreCase)
            || execution.Candles.Count < 2 || !HasExactSafeSeries(execution)
            || laterConfirmation && (execution.AsOfUtc <= primarySeries.AsOfUtc
                || execution.AsOfUtc > primarySeries.AsOfUtc.AddHours(3)))
            return ExperimentAnalysisResult.Blocked(
                "Relative-strength pullback requires a closed one-hour confirmation strictly after the four-hour setup.");
        var setup = primarySeries.Candles;
        var confirmation = execution.Candles;
        var fastEmaPeriod = parameters.Int32("pullbackFastEma");
        var slowEmaPeriod = parameters.Int32("pullbackSlowEma");
        var ema20 = new ExponentialMovingAverageCalculator(fastEmaPeriod).Calculate(setup).Value!.Value;
        var ema50 = new ExponentialMovingAverageCalculator(slowEmaPeriod).Calculate(setup).Value!.Value;
        var atr = new AverageTrueRangeCalculator(parameters.Int32("atrPeriod")).Calculate(setup).Value!.Value;
        var rsiPeriod = parameters.Int32("rsiPeriod");
        var rsi = new RelativeStrengthIndexCalculator(rsiPeriod).Calculate(setup).Value!.Value;
        var priorRsi = new RelativeStrengthIndexCalculator(rsiPeriod)
            .Calculate(setup.Take(setup.Count - 1).ToArray()).Value!.Value;
        var distance = Math.Min(decimal.Abs(setup[^1].Close - ema20), decimal.Abs(setup[^1].Close - ema50));
        if (!top || selected.LongReturn <= 0m || !selected.AboveEma200
            || selected.MedianQuoteVolume < parameters.Decimal("minimumLiquidity")
            || selected.Volatility > parameters.Decimal("maximumVolatility")
            || setup[^1].Close < ema50
            || distance > atr * parameters.Decimal("maximumPullbackAtr"))
        {
            return BearishExit(
                "platform.relative-strength-pullback-rotation",
                "Relative-strength rank, daily trend, liquidity, volatility, or four-hour structure invalidation requires a Spot exit.");
        }
        return ExperimentAnalysisResult.FromConsensus(
            "platform.relative-strength-pullback-rotation",
            [
                Check("top-relative-strength", top && (!revised || selected.BenchmarkExcessReturn > 0m),
                    $"Asset ranks in the top {parameters.Decimal("topRankPercent")}% and exceeds same-quote XBT/EUR over the long daily lookback."),
                Check("positive-daily-trend", selected.LongReturn > 0m && selected.AboveEma200
                    && selected.MedianQuoteVolume >= parameters.Decimal("minimumLiquidity")
                    && selected.Volatility <= parameters.Decimal("maximumVolatility"),
                    "Daily absolute trend, configured liquidity, and volatility limits pass."),
                Check("bounded-four-hour-pullback",
                    distance <= atr * parameters.Decimal("maximumPullbackAtr") && setup[^1].Close >= ema50,
                    $"Four-hour pullback is within {parameters.Decimal("maximumPullbackAtr")} ATR of EMA {fastEmaPeriod} or {slowEmaPeriod} without structural failure."),
                Check("four-hour-rsi-cooled", rsi >= parameters.Decimal("pullbackRsiMinimum")
                    && rsi <= parameters.Decimal("pullbackRsiMaximum") && rsi >= priorRsi,
                    "Four-hour RSI has cooled within the configured zone and stopped deteriorating."),
                Check("one-hour-confirmation", confirmation[^1].Close > confirmation[^2].High,
                    laterConfirmation ? "A later closed one-hour candle confirms renewed upside after the four-hour setup."
                        : "A closed one-hour candle confirms renewed upside.")
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
                || candle.Close <= 0m
                || candle.CloseTimeUtc > series.AsOfUtc
                || (previous is not null && candle.OpenTimeUtc != previous.CloseTimeUtc))
                return false;
            previous = candle;
        }
        return true;
    }
}
