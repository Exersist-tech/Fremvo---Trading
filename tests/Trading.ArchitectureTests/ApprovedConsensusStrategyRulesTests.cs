using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.ArchitectureTests;

public sealed class ApprovedConsensusStrategyRulesTests
{
    private static readonly DateTimeOffset AsOfUtc =
        new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CatalogueUsesTheAttachedRegimeSignalAndExecutionProfiles()
    {
        Assert.Contains(
            new ApprovedStrategyTimeframeProfile(
                CandleInterval.OneHour,
                CandleInterval.FiveMinutes,
                CandleInterval.OneMinute),
            ApprovedConsensusStrategyProfiles.For("platform.ema-trend-continuation"));
        Assert.Contains(
            new ApprovedStrategyTimeframeProfile(
                CandleInterval.OneDay,
                CandleInterval.FourHours,
                CandleInterval.OneHour),
            ApprovedConsensusStrategyProfiles.For("platform.relative-strength-pullback-rotation"));
        Assert.Equal(
            new ApprovedStrategyTimeframeProfile(
                CandleInterval.OneDay,
                CandleInterval.OneDay,
                CandleInterval.FourHours),
            Assert.Single(ApprovedConsensusStrategyProfiles.For("platform.cross-sectional-momentum-rotation")));
    }

    [Fact]
    public void ExactRuntimeDefinitionsAreVersionedAndRequireImmutableDefaults()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();

        Assert.All(registry.Definitions, definition =>
        {
            Assert.Equal(2, definition.Version);
            Assert.Equal(2, definition.ParameterSchemaVersion);
        });

        var result = registry.EvaluateProfile(
            "platform.ema-trend-continuation",
            Rising(CandleInterval.OneHour),
            Rising(CandleInterval.FiveMinutes),
            Rising(CandleInterval.OneMinute),
            """{"regimeFastPeriod":10}""");

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.True(result.Consensus!.MandatoryVeto);
        Assert.Contains("immutable", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("platform.ema-trend-continuation", "regime-close-above-ema200", 4)]
    [InlineData("platform.donchian-breakout-ensemble", "donchian100", 4)]
    [InlineData("platform.bollinger-mean-reversion", "ranging-adx", 5)]
    [InlineData("platform.rsi-pullback", "rsi-pullback-turn", 4)]
    [InlineData("platform.macd-volume", "macd-cross", 4)]
    [InlineData("platform.volatility-compression-breakout", "low-bandwidth-percentile", 4)]
    [InlineData("platform.session-conditioned-breakout", "versioned-session-profile", 5)]
    [InlineData("platform.regime-switching-ensemble", "long-term-trend", 5)]
    public void CandleFamiliesEmitTheSpecifiedFiveChecks(
        string familyId,
        string requiredCheck,
        int threshold)
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var profile = ApprovedConsensusStrategyProfiles.For(familyId)[0];

        var result = registry.EvaluateProfile(
            familyId,
            Rising(profile.Regime),
            Rising(profile.Signal),
            Rising(profile.Execution),
            "{}");

        Assert.NotNull(result.Consensus);
        Assert.Equal(5, result.Consensus.Checks.Count);
        Assert.Contains(result.Consensus.Checks, check => check.Id == requiredCheck);
        Assert.Equal(threshold, result.Consensus.RequiredAgreement);
    }

    [Fact]
    public void EmaAndDonchianFailClosedWithoutTheirExactWarmup()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var ema = registry.EvaluateProfile(
            "platform.ema-trend-continuation",
            Rising(CandleInterval.OneHour, 100),
            Rising(CandleInterval.FiveMinutes, 50),
            Rising(CandleInterval.OneMinute, 1),
            "{}");
        var donchian = registry.EvaluateProfile(
            "platform.donchian-breakout-ensemble",
            Rising(CandleInterval.FourHours, 100),
            Rising(CandleInterval.FifteenMinutes, 100),
            Rising(CandleInterval.FifteenMinutes, 100),
            "{}");

        Assert.All([ema, donchian], result =>
        {
            Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
            Assert.True(result.Consensus!.MandatoryVeto);
        });
        Assert.Contains("201 regime", ema.Reason, StringComparison.Ordinal);
        Assert.Contains("101 signal", donchian.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DonchianBearishConsensusProducesAReduceSignal()
    {
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            "platform.donchian-breakout-ensemble",
            Falling(CandleInterval.FourHours),
            Falling(CandleInterval.FifteenMinutes),
            Falling(CandleInterval.FifteenMinutes),
            "{}");

        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, result.Outcome);
        Assert.True(result.Value < 0m);
        Assert.Equal(5, result.Consensus!.BearishCount);
    }

    [Fact]
    public void ExactEvaluatorRejectsAnUnapprovedTimeframeCombination()
    {
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            "platform.ema-trend-continuation",
            Rising(CandleInterval.OneHour),
            Rising(CandleInterval.FifteenMinutes),
            Rising(CandleInterval.OneMinute),
            "{}");

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.Contains("timeframe", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossSectionalRankingUsesRanksBenchmarkAndMedianEvidence()
    {
        var universe = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase)
        {
            ["XBT/EUR"] = Daily(100m, .20m, 10_000m),
            ["ETH/EUR"] = Daily(80m, .40m, 20_000m),
            ["SOL/EUR"] = Daily(50m, -.10m, 5_000m)
        };

        var ranked = CrossSectionalConsensusRanking.Evaluate(universe);
        var eth = Assert.Single(ranked, item => item.Symbol == "ETH/EUR");
        var sol = Assert.Single(ranked, item => item.Symbol == "SOL/EUR");

        Assert.Equal(3, ranked.Length);
        Assert.True(eth.MomentumScore > sol.MomentumScore);
        Assert.True(eth.RelativeStrengthScore > sol.RelativeStrengthScore);
        Assert.NotEqual(0m, eth.CorrelationToBenchmark);
    }

    [Fact]
    public void SessionStrategyCannotPassWithoutMeasuredCostsAndBaselineApproval()
    {
        var profile = ApprovedConsensusStrategyProfiles.For("platform.session-conditioned-breakout")[0];
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            "platform.session-conditioned-breakout",
            Rising(profile.Regime),
            Rising(profile.Signal),
            Rising(profile.Execution),
            "{}");

        Assert.NotEqual(ExperimentAnalysisOutcome.Analyzed, result.Outcome);
        Assert.All(
            result.Consensus!.Checks.Where(check => check.Id is "session-cost-gates" or "validated-session-baseline"),
            check => Assert.Equal(ExperimentSignalDirection.Neutral, check.Direction));
    }

    [Fact]
    public void MaximumHoldingPolicyUsesTheVersionedSignalCandleLimit()
    {
        var openedAt = AsOfUtc.AddMinutes(-200);
        var worker = new Trading.Domain.Experiments.ExperimentWorker(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "worker",
            "platform.ema-trend-continuation",
            "XBT/EUR",
            1_000m,
            openedAt,
            1);
        var position = new ExperimentProtectiveExitPosition(
            worker.UserId,
            worker.Id,
            Guid.NewGuid(),
            worker.MarketSymbol,
            1m,
            100m,
            openedAt,
            90m,
            120m,
            worker,
            CandleInterval.FiveMinutes);

        Assert.False(ApprovedStrategyHoldingPolicy.IsExpired(position, AsOfUtc.AddTicks(-1)));
        Assert.True(ApprovedStrategyHoldingPolicy.IsExpired(position, AsOfUtc));
    }

    [Fact]
    public void FamilySpecificInvalidationUsesTheApprovedSignalExit()
    {
        var falling = Falling(CandleInterval.FifteenMinutes).Candles;

        var result = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation",
            falling);

        Assert.True(result.ShouldExit);
        Assert.Contains("EMA 50", result.Reason, StringComparison.Ordinal);
    }

    private static ExperimentCandleSeries Rising(
        CandleInterval interval,
        int count = ApprovedConsensusStrategyProfiles.RequiredHistory)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var closeBoundary = new DateTimeOffset(
            AsOfUtc.UtcTicks - AsOfUtc.UtcTicks % duration.Ticks,
            TimeSpan.Zero);
        var candles = Enumerable.Range(0, count)
            .Select(index =>
            {
                var openTime = closeBoundary.AddTicks(duration.Ticks * (index - count));
                var price = 100m + index;
                return new Candle(
                    "XBT/EUR",
                    interval,
                    openTime,
                    openTime.Add(duration),
                    price,
                    price + 1m,
                    price - 1m,
                    price + .5m,
                    index == count - 1 ? 2m : 1m,
                    true,
                    false);
            })
            .ToArray();
        return new ExperimentCandleSeries(
            "XBT/EUR",
            interval,
            closeBoundary,
            candles);
    }

    private static ExperimentCandleSeries Falling(CandleInterval interval)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var closeBoundary = new DateTimeOffset(
            AsOfUtc.UtcTicks - AsOfUtc.UtcTicks % duration.Ticks,
            TimeSpan.Zero);
        var count = ApprovedConsensusStrategyProfiles.RequiredHistory;
        var candles = Enumerable.Range(0, count)
            .Select(index =>
            {
                var openTime = closeBoundary.AddTicks(duration.Ticks * (index - count));
                var price = 320m - index;
                return new Candle(
                    "XBT/EUR",
                    interval,
                    openTime,
                    openTime.Add(duration),
                    price,
                    price + 1m,
                    price - 1m,
                    price - .5m,
                    index == count - 1 ? 2m : 1m,
                    true,
                    false);
            })
            .ToArray();
        return new ExperimentCandleSeries("XBT/EUR", interval, closeBoundary, candles);
    }

    private static Candle[] Daily(
        decimal initial,
        decimal totalReturn,
        decimal volume)
    {
        const int count = ApprovedConsensusStrategyProfiles.RequiredHistory;
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var openTime = AsOfUtc.AddDays(index - count);
                var progress = index / (decimal)(count - 1);
                var price = initial * (1m + totalReturn * progress);
                return new Candle(
                    "rank/EUR",
                    CandleInterval.OneDay,
                    openTime,
                    openTime.AddDays(1),
                    price,
                    price * 1.01m,
                    price * .99m,
                    price,
                    volume,
                    true,
                    false);
            })
            .ToArray();
    }
}
