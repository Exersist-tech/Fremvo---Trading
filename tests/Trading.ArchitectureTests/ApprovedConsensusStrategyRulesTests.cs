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
        Assert.Contains(
            CandleInterval.OneHour,
            ApprovedConsensusStrategyProfiles.RequiredIntervals(
                "platform.three-swing-channel-divergence",
                CandleInterval.FiveMinutes));
    }

    [Fact]
    public void ExactRuntimeDefinitionsAreVersionedAndRejectUnapprovedSettings()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();

        Assert.All(registry.Definitions, definition =>
        {
            var currentVersion = definition.FamilyId switch
            {
                "platform.relative-strength-pullback-rotation"
                    or "platform.donchian-breakout-ensemble"
                    or "platform.bollinger-mean-reversion"
                    or "platform.rsi-pullback"
                    or "platform.macd-volume"
                    or "platform.ema-trend-continuation"
                    or "platform.volatility-compression-breakout"
                    or "platform.regime-switching-ensemble" => 5,
                "platform.cross-sectional-momentum-rotation"
                    or "platform.three-swing-channel-divergence" => 4,
                _ => 3
            };
            Assert.Equal(currentVersion, definition.Version);
            Assert.Equal(currentVersion, definition.ParameterSchemaVersion);
            var prior = registry.ResolveDefinition(definition.FamilyId, 2);
            Assert.Equal(2, prior.Version);
            Assert.Equal(2, prior.ParameterSchemaVersion);
            Assert.NotEqual(prior.ContentFingerprint, definition.ContentFingerprint);
        });
        Assert.Equal(3, registry.ResolveDefinition("platform.relative-strength-pullback-rotation", 3).Version);
        Assert.Equal(4, registry.ResolveDefinition("platform.relative-strength-pullback-rotation", 4).Version);
        Assert.Equal(3, registry.ResolveDefinition("platform.regime-switching-ensemble", 3).Version);

        var result = registry.EvaluateProfile(
            "platform.ema-trend-continuation",
            Rising(CandleInterval.OneHour),
            Rising(CandleInterval.FiveMinutes),
            Rising(CandleInterval.OneMinute),
            """{"regimeFastPeriod":10}""");

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.True(result.Consensus!.MandatoryVeto);
        Assert.Contains("not an editable parameter", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StrategySettingsNormalizeDefaultsValidateBoundsAndReachEvaluators()
    {
        Assert.Equal(11, ApprovedStrategyParameters.Catalog.Count);
        foreach (var strategy in ApprovedStrategyParameters.Catalog)
        {
            Assert.True(ApprovedStrategyParameters.TryNormalize(strategy.StrategyId, "{}", out var defaults, out var error), error);
            using var document = System.Text.Json.JsonDocument.Parse(defaults);
            Assert.Equal(strategy.Fields.Count, document.RootElement.EnumerateObject().Count());
            var values = ApprovedStrategyParameters.Read(strategy.StrategyId, defaults);
            foreach (var field in strategy.Fields)
            {
                switch (field.Type)
                {
                    case "number":
                        Assert.Equal(field.DefaultNumber, values.Decimal(field.Key));
                        break;
                    case "integer":
                        Assert.Equal(decimal.ToInt32(field.DefaultNumber!.Value), values.Int32(field.Key));
                        break;
                    case "select":
                        Assert.Equal(field.DefaultText, values.String(field.Key));
                        break;
                    case "boolean":
                        Assert.Equal(field.DefaultText == "true", values.Boolean(field.Key));
                        break;
                }
            }
        }

        const string emaStrategy = "platform.ema-trend-continuation";
        Assert.False(ApprovedStrategyParameters.TryNormalize(
            emaStrategy,
            """{"regimeFastEma":80,"regimeSlowEma":60}""",
            out _,
            out var relationError));
        Assert.Contains("strictly ordered", relationError, StringComparison.Ordinal);
        Assert.False(ApprovedStrategyParameters.TryNormalize(
            emaStrategy,
            """{"regimeFastEma":301}""",
            out _,
            out var rangeError));
        Assert.Contains("between", rangeError, StringComparison.Ordinal);
        Assert.False(ApprovedStrategyParameters.TryNormalize(
            emaStrategy,
            """{"regimeFastPeriod":10}""",
            out _,
            out var unknownError));
        Assert.Contains("not an editable parameter", unknownError, StringComparison.Ordinal);
        var momentumDefaults = ApprovedStrategyParameters.For("platform.cross-sectional-momentum-rotation").DefaultsJson;
        using var momentumDocument = System.Text.Json.JsonDocument.Parse(momentumDefaults);
        var invalidMomentumWeights = momentumDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        invalidMomentumWeights["momentumMediumWeight"] =
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("0.5");
        Assert.False(ApprovedStrategyParameters.TryNormalize(
            "platform.cross-sectional-momentum-rotation",
            System.Text.Json.JsonSerializer.Serialize(invalidMomentumWeights),
            out _,
            out var weightError));
        Assert.Contains("weights must sum to one", weightError, StringComparison.Ordinal);

        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var customized = registry.EvaluateProfile(
            emaStrategy,
            Rising(CandleInterval.OneHour),
            Rising(CandleInterval.FiveMinutes),
            Rising(CandleInterval.OneMinute),
            """{"minimumAgreement":5}""");
        Assert.Equal(5, customized.Consensus!.RequiredAgreement);
    }

    [Theory]
    [InlineData("platform.ema-trend-continuation")]
    [InlineData("platform.donchian-breakout-ensemble")]
    [InlineData("platform.rsi-pullback")]
    [InlineData("platform.macd-volume")]
    [InlineData("platform.volatility-compression-breakout")]
    public void ThreeConfirmationsCannotBeSavedOrEvaluated(string family)
    {
        Assert.False(ApprovedStrategyParameters.TryNormalize(
            family, """{"minimumAgreement":3}""", out _, out var error));
        Assert.Contains("between 4 and 5", error, StringComparison.Ordinal);

        var profile = ApprovedConsensusStrategyProfiles.For(family)[0];
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            family,
            Rising(profile.Regime),
            Rising(profile.Signal),
            Rising(profile.Execution),
            """{"minimumAgreement":3}""");
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.True(result.Consensus!.MandatoryVeto);
    }

    [Theory]
    [InlineData("platform.ema-trend-continuation", "bounded-pullback")]
    [InlineData("platform.donchian-breakout-ensemble", "breakout-volume")]
    [InlineData("platform.rsi-pullback", "rsi-pullback-turn")]
    [InlineData("platform.macd-volume", "macd-cross")]
    [InlineData("platform.volatility-compression-breakout", "closed-channel-break")]
    public void ApprovedFamiliesIdentifyTheirIndispensableEntryCheck(string family, string trigger)
    {
        var profile = ApprovedConsensusStrategyProfiles.For(family)[0];
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            family,
            Rising(profile.Regime),
            Rising(profile.Signal),
            Rising(profile.Execution),
            "{}");

        Assert.NotNull(result.Consensus);
        Assert.Contains(trigger, result.Consensus.RequiredEntryChecks);
    }

    [Theory]
    [InlineData("platform.ema-trend-continuation", "bounded-pullback")]
    [InlineData("platform.rsi-pullback", "rsi-pullback-turn")]
    public void FourOtherPositiveVotesCannotReplaceANamedEntryTrigger(string family, string trigger)
    {
        var profile = ApprovedConsensusStrategyProfiles.For(family)[0];
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            family,
            Rising(profile.Regime),
            Rising(profile.Signal),
            Rising(profile.Execution),
            "{}");

        Assert.Equal(ExperimentAnalysisOutcome.NoCondition, result.Outcome);
        Assert.Equal(4, result.Consensus!.BullishCount);
        Assert.Contains(trigger, result.Consensus.RequiredEntryChecks);
        Assert.Contains(trigger, result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderAdmittedEmaVersionRetainsItsOriginalConsensusDecision()
    {
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var regime = Rising(CandleInterval.OneHour);
        var signal = Rising(CandleInterval.FiveMinutes);
        var execution = Rising(CandleInterval.OneMinute);
        var original = registry.EvaluateProfile(
            "platform.ema-trend-continuation", regime, signal, execution, "{}", strategyVersion: 2);
        var revised = registry.EvaluateProfile(
            "platform.ema-trend-continuation", regime, signal, execution, "{}", strategyVersion: 3);

        Assert.Equal(ExperimentAnalysisOutcome.Analyzed, original.Outcome);
        Assert.Equal(ExperimentAnalysisOutcome.NoCondition, revised.Outcome);
        Assert.Empty(original.Consensus!.RequiredEntryChecks);
        Assert.Contains("bounded-pullback", revised.Consensus!.RequiredEntryChecks);
    }

    [Fact]
    public void EmaSlopeSettingsRequireEnoughRegimeHistoryBeforeIndicatorCalculation()
    {
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            "platform.ema-trend-continuation",
            Rising(CandleInterval.OneHour, 110),
            Rising(CandleInterval.FiveMinutes),
            Rising(CandleInterval.OneMinute),
            """{"regimeFastEma":100,"regimeSlowEma":101,"regimeSlopeLookback":30}""");

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Outcome);
        Assert.Contains("130 regime", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("platform.donchian-breakout-ensemble", """{"regimeEma":150}""", "regime-ema150")]
    [InlineData("platform.bollinger-mean-reversion", """{"flatEmaPeriod":60}""", "flat-ema60")]
    [InlineData("platform.rsi-pullback", """{"regimeFastEma":40,"regimeSlowEma":150}""", "regime-ema40-above-ema150")]
    public void CustomizedIndicatorPeriodsAppearInTheActualDecisionChecks(
        string family, string settings, string checkId)
    {
        var profile = ApprovedConsensusStrategyProfiles.For(family)[0];
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            family, Rising(profile.Regime), Rising(profile.Signal), Rising(profile.Execution), settings);

        Assert.Contains(result.Consensus!.Checks, check => check.Id == checkId);
        Assert.Contains(checkId, result.Consensus.RequiredEntryChecks);
    }

    [Theory]
    [InlineData("platform.donchian-breakout-ensemble", """{"regimeEma":150}""", "regime-ema200")]
    [InlineData("platform.bollinger-mean-reversion", """{"flatEmaPeriod":60}""", "flat-ema50")]
    [InlineData("platform.rsi-pullback", """{"regimeFastEma":40,"regimeSlowEma":150}""", "regime-ema50-above-ema200")]
    public void AdmittedVersionTwoKeepsItsHistoricalCheckIdentities(
        string family, string settings, string originalId)
    {
        var profile = ApprovedConsensusStrategyProfiles.For(family)[0];
        var result = ApprovedExperimentStrategyRegistry.CreatePlatformDefault().EvaluateProfile(
            family, Rising(profile.Regime), Rising(profile.Signal), Rising(profile.Execution),
            settings, strategyVersion: 2);

        Assert.Contains(result.Consensus!.Checks, check => check.Id == originalId);
        Assert.Empty(result.Consensus.RequiredEntryChecks);
    }

    [Fact]
    public void ThreeSwingStrategyRequiresHourlyContextAndEmitsFiveAuditableChecks()
    {
        var family = "platform.three-swing-channel-divergence";
        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var regime = Rising(CandleInterval.FourHours);
        var signal = Rising(CandleInterval.FiveMinutes);
        var execution = Rising(CandleInterval.FiveMinutes);

        var missingContext = registry.EvaluateProfile(family, regime, signal, execution, "{}");
        var withContext = registry.EvaluateProfile(
            family,
            regime,
            signal,
            execution,
            "{}",
            Rising(CandleInterval.OneHour));

        Assert.Equal(ExperimentAnalysisOutcome.Blocked, missingContext.Outcome);
        Assert.True(missingContext.Consensus!.MandatoryVeto);
        Assert.Equal(ExperimentAnalysisOutcome.NoCondition, withContext.Outcome);
        Assert.Equal(5, withContext.Consensus!.Checks.Count);
        Assert.Contains(withContext.Consensus.Checks, check => check.Id == "three-swing-rsi-divergence");
        Assert.Contains(withContext.Consensus.Checks, check => check.Id == "macd-confirmation");
        Assert.Contains("closed-reversal-confirmation", withContext.Consensus.RequiredEntryChecks);
        Assert.Contains("macd-confirmation", withContext.Consensus.RequiredEntryChecks);

        var optional = registry.EvaluateProfile(
            family, regime, signal, execution,
            """{"requireClosedReversal":false,"requireMacdConfirmation":false}""",
            Rising(CandleInterval.OneHour));
        Assert.Equal(3, optional.Consensus!.RequiredEntryChecks.Count);
    }

    [Theory]
    [InlineData("platform.ema-trend-continuation", "regime-close-above-ema200", 4)]
    [InlineData("platform.donchian-breakout-ensemble", "donchian100", 4)]
    [InlineData("platform.bollinger-mean-reversion", "ranging-adx", 5)]
    [InlineData("platform.rsi-pullback", "rsi-pullback-turn", 4)]
    [InlineData("platform.macd-volume", "macd-cross", 4)]
    [InlineData("platform.volatility-compression-breakout", "low-bandwidth-percentile", 4)]
    [InlineData("platform.session-conditioned-breakout", "versioned-session-profile", 5)]
    [InlineData("platform.regime-switching-ensemble", "long-term-trend-ema200", 5)]
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
    public void CompressionPercentilesUseOnlyPriorObservationsInVersionFour()
    {
        var bandwidth = new[] { 10m, 10m, 10m, 5m, 5m, 4m, 3m, 2m };
        Assert.True(ApprovedConsensusStrategyRules.HasPersistentCompression(bandwidth, 3, 30m, pastOnly: true));
        Assert.False(ApprovedConsensusStrategyRules.HasPersistentCompression(bandwidth, 3, 30m, pastOnly: false));
        Assert.False(ApprovedConsensusStrategyRules.HasPersistentCompression([10m, 2m], 2, 30m, pastOnly: true));

        var registry = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        Assert.Equal(4, registry.ResolveDefinition("platform.volatility-compression-breakout", 4).Version);
        Assert.Equal(3, registry.ResolveDefinition("platform.volatility-compression-breakout", 3).Version);
        var profile = ApprovedConsensusStrategyProfiles.For("platform.volatility-compression-breakout")[0];
        var shortSignal = Rising(profile.Signal, 22);
        var insufficient = registry.EvaluateProfile("platform.volatility-compression-breakout",
            Rising(profile.Regime), shortSignal, shortSignal, "{}", strategyVersion: 4);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, insufficient.Outcome);
        Assert.Contains("125 signal", insufficient.Reason, StringComparison.Ordinal);
        var nearlyReady = Rising(profile.Signal, 124);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, registry.EvaluateProfile(
            "platform.volatility-compression-breakout", Rising(profile.Regime), nearlyReady,
            nearlyReady, "{}", strategyVersion: 4).Outcome);
        var ready = Rising(profile.Signal, 125);
        var evaluated = registry.EvaluateProfile("platform.volatility-compression-breakout",
            Rising(profile.Regime), ready, ready, "{}", strategyVersion: 4);
        Assert.NotNull(evaluated.Consensus);
        Assert.False(evaluated.Consensus.MandatoryVeto);
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
        var customWeights = CrossSectionalConsensusRanking.Evaluate(
            universe,
            weights: CrossSectionalRankingWeights.PlatformDefault with
            {
                MomentumMedium = 1m,
                MomentumLong = 0m,
                MomentumTrend = 0m,
                MomentumLiquidity = 0m,
                MomentumVolatilityPenalty = 0m,
                MomentumTurnoverPenalty = 0m
            });
        Assert.Contains(ranked, original => original.MomentumScore !=
            Assert.Single(customWeights, item => item.Symbol == original.Symbol).MomentumScore);
    }

    [Fact]
    public void RevisedRelativeStrengthUsesDailyExcessBreadthRatherThanDuplicateLongReturnRanks()
    {
        var steady = Daily(100m, .20m, 10_000m);
        var volatilePath = steady.Select((candle, index) =>
        {
            var price = index == steady.Length - 1 || index == steady.Length - 91 || index % 2 == 0
                ? candle.Close : candle.Close * 1.01m;
            return new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                price, price * 1.02m, price * .98m, price, candle.Volume, true, false);
        }).ToArray();
        var universe = new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["XBT/EUR"] = Daily(100m, .10m, 10_000m),
            ["ETH/EUR"] = steady,
            ["SOL/EUR"] = volatilePath
        };
        var weights = CrossSectionalRankingWeights.PlatformDefault with
        {
            RelativeMedium = 0m,
            RelativeLong = 0m,
            RelativeBenchmark = 1m,
            RelativeMedian = 0m,
            RelativeRiskAdjusted = 0m
        };
        var oldRanking = CrossSectionalConsensusRanking.Evaluate(universe, weights: weights);
        var dailyRanking = CrossSectionalConsensusRanking.Evaluate(universe, weights: weights,
            dailyExcessBreadth: true);
        var oldEth = Assert.Single(oldRanking, item => item.Symbol == "ETH/EUR");
        var oldSol = Assert.Single(oldRanking, item => item.Symbol == "SOL/EUR");
        var newEth = Assert.Single(dailyRanking, item => item.Symbol == "ETH/EUR");
        var newSol = Assert.Single(dailyRanking, item => item.Symbol == "SOL/EUR");

        Assert.Equal(oldEth.RelativeStrengthScore, oldSol.RelativeStrengthScore);
        Assert.True(newEth.RelativeStrengthScore > newSol.RelativeStrengthScore);
        Assert.True(newEth.BenchmarkExcessReturn > 0m);
        Assert.Equal(newEth.BenchmarkExcessReturn, newSol.BenchmarkExcessReturn);
        Assert.Empty(CrossSectionalConsensusRanking.Evaluate(
            universe.ToDictionary(item => item.Key, item => item.Key == "SOL/EUR"
                ? (IReadOnlyList<Candle>)item.Value.Select(candle =>
                    new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc.AddDays(1),
                        candle.CloseTimeUtc.AddDays(1), candle.Open, candle.High, candle.Low,
                        candle.Close, candle.Volume, true, false)).ToArray()
                : item.Value),
            weights: weights, dailyExcessBreadth: true));
    }

    [Fact]
    public void RankingRefusesZeroCloseInsteadOfDividingByZero()
    {
        var benchmark = Daily(100m, .1m, 10_000m);
        var invalid = Daily(100m, .2m, 10_000m);
        var latest = invalid[^1];
        invalid[^1] = new Candle(latest.Symbol, latest.Interval, latest.OpenTimeUtc,
            latest.CloseTimeUtc, latest.Open, latest.High, 0m, 0m, latest.Volume, true, false);
        Assert.Empty(CrossSectionalConsensusRanking.Evaluate(new Dictionary<string, IReadOnlyList<Candle>>
        {
            ["XBT/EUR"] = benchmark,
            ["ETH/EUR"] = invalid
        }, dailyExcessBreadth: true));
    }

    [Fact]
    public void LegacyRelativeRankingSettingsRequireExplicitSelectionOfTheNewModel()
    {
        const string family = "platform.relative-strength-pullback-rotation";
        Assert.True(ApprovedStrategyParameters.TryNormalize(family, """{"topRankPercent":25}""",
            out var migrated, out var error), error);
        Assert.Equal("legacyRanks", ApprovedStrategyParameters.Read(family, migrated).String("relativeRankingModel"));
        Assert.Equal("legacyAtrPlan", ApprovedStrategyParameters.Read(family, migrated).String("relativePlanModel"));
        Assert.Equal("dailyExcessBreadth",
            ApprovedStrategyParameters.Read(family, "{}").String("relativeRankingModel"));
        Assert.Equal("fourHourStructure",
            ApprovedStrategyParameters.Read(family, "{}").String("relativePlanModel"));
        Assert.True(ApprovedStrategyParameters.TryNormalize(family,
            """{"relativeRankingModel":"dailyExcessBreadth","relativePlanModel":"fourHourStructure","stopSwingLookback":8,"targetChannelLookback":18,"minimumRewardRisk":1.5}""",
            out var reviewed, out error), error);
        var reviewedValues = ApprovedStrategyParameters.Read(family, reviewed);
        Assert.Equal(8, reviewedValues.Int32("stopSwingLookback"));
        Assert.Equal(18, reviewedValues.Int32("targetChannelLookback"));
        Assert.Equal(1.5m, reviewedValues.Decimal("minimumRewardRisk"));
        Assert.False(ApprovedStrategyParameters.TryNormalize(family,
            """{"stopSwingLookback":20,"targetChannelLookback":5}""", out _, out _));
    }

    [Fact]
    public void LegacyDonchianSettingsKeepTheAtrPlanUntilTheOwnerReviewsTheStructuralPlan()
    {
        const string family = "platform.donchian-breakout-ensemble";
        Assert.True(ApprovedStrategyParameters.TryNormalize(family, """{"shortChannel":25}""",
            out var legacy, out var error), error);
        Assert.Equal("legacyAtrPlan",
            ApprovedStrategyParameters.Read(family, legacy).String("donchianPlanModel"));
        Assert.Equal("priorBreakRange",
            ApprovedStrategyParameters.Read(family, "{}").String("donchianPlanModel"));
        Assert.True(ApprovedStrategyParameters.TryNormalize(family,
            """{"donchianPlanModel":"priorBreakRange","shortChannel":25,"stopBufferAtr":0.3,"targetRiskMultiple":2}""",
            out var reviewed, out error), error);
        Assert.Equal(.3m, ApprovedStrategyParameters.Read(family, reviewed).Decimal("stopBufferAtr"));
        Assert.Equal(2m, ApprovedStrategyParameters.Read(family, reviewed).Decimal("targetRiskMultiple"));
        Assert.False(ApprovedStrategyParameters.TryNormalize(family,
            """{"stopBufferAtr":3}""", out _, out _));
    }

    [Fact]
    public void MomentumVolatilityPenaltyIsLargestForTheMostVolatileAsset()
    {
        var universe = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase)
        {
            ["XBT/EUR"] = Daily(100m, .20m, 10_000m),
            ["ETH/EUR"] = Daily(80m, .40m, 20_000m),
            ["SOL/EUR"] = Daily(50m, -.10m, 5_000m)
        };
        var unpenalized = CrossSectionalConsensusRanking.Evaluate(universe,
            weights: CrossSectionalRankingWeights.PlatformDefault with { MomentumVolatilityPenalty = 0m });
        var penalized = CrossSectionalConsensusRanking.Evaluate(universe,
            weights: CrossSectionalRankingWeights.PlatformDefault with { MomentumVolatilityPenalty = 1m });
        var lowest = penalized.MinBy(asset => asset.Volatility)!;
        var highest = penalized.MaxBy(asset => asset.Volatility)!;
        Assert.True(lowest.Volatility < highest.Volatility);

        decimal Deduction(CrossSectionalRankEvidence asset) =>
            unpenalized.Single(item => item.Symbol == asset.Symbol).MomentumScore - asset.MomentumScore;
        Assert.True(Deduction(lowest) < Deduction(highest));
        Assert.Equal(0m, Deduction(lowest));
        Assert.Equal(1m, Deduction(highest));
    }

    [Fact]
    public void IdenticalVolatilityReceivesIdenticalRankingPenalty()
    {
        var universe = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.OrdinalIgnoreCase)
        {
            ["XBT/EUR"] = Daily(100m, .20m, 10_000m),
            ["ETH/EUR"] = Daily(100m, .20m, 20_000m)
        };
        var unpenalized = CrossSectionalConsensusRanking.Evaluate(universe,
            weights: CrossSectionalRankingWeights.PlatformDefault with { MomentumVolatilityPenalty = 0m });
        var penalized = CrossSectionalConsensusRanking.Evaluate(universe,
            weights: CrossSectionalRankingWeights.PlatformDefault with { MomentumVolatilityPenalty = 1m });

        Assert.Equal(penalized[0].Volatility, penalized[1].Volatility);
        Assert.Equal(
            unpenalized[0].MomentumScore - penalized[0].MomentumScore,
            unpenalized[1].MomentumScore - penalized[1].MomentumScore);
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
        Assert.Contains("session-cost-gates=Neutral", result.Reason, StringComparison.Ordinal);
        Assert.Contains("validated-session-baseline=Neutral", result.Reason, StringComparison.Ordinal);
        Assert.True(result.Reason.Length <= 512);
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

    [Fact]
    public void NewVersionedExitsUseSavedSignalPeriodsWithoutChangingReservedLegacyRules()
    {
        var donchian = FlatExitHistory(105m, 100m, 110m,
            (40, 105m, 90m, 110m), (59, 95m, 94m, 110m));
        var newChannel = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.donchian-breakout-ensemble", donchian, 4, """{"exitChannel":10}""",
            donchian[^2].CloseTimeUtc);
        var oldChannel = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.donchian-breakout-ensemble", donchian, 3, """{"exitChannel":10}""");
        Assert.True(newChannel.ShouldExit);
        Assert.Contains("Donchian 10", newChannel.Reason, StringComparison.Ordinal);
        Assert.False(oldChannel.ShouldExit);

        var reversion = FlatExitHistory(100m, 99m, 101m,
            (40, 120m, 119m, 121m), (41, 120m, 119m, 121m),
            (42, 120m, 119m, 121m), (43, 120m, 119m, 121m),
            (44, 120m, 119m, 121m), (45, 120m, 119m, 121m),
            (46, 120m, 119m, 121m), (47, 120m, 119m, 121m),
            (48, 120m, 119m, 121m), (49, 120m, 119m, 121m),
            (59, 110m, 109m, 111m));
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.bollinger-mean-reversion", reversion, 4,
            """{"bollingerPeriod":10}""", reversion[^2].CloseTimeUtc).ShouldExit);
        Assert.False(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.bollinger-mean-reversion", reversion, 3,
            """{"bollingerPeriod":10}""").ShouldExit);

        var compression = FlatExitHistory(100m, 99m, 101m,
            (40, 100m, 99m, 110m), (57, 105m, 99m, 106m),
            (58, 106m, 99m, 107m), (59, 105m, 99m, 106m));
        Assert.False(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", compression, 4,
            """{"exitChannel":5}""", compression[57].CloseTimeUtc).ShouldExit);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", compression, 3,
            """{"exitChannel":5}""", compression[57].CloseTimeUtc).ShouldExit);
        Assert.Contains("attested UTC opening signal close", ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", compression, 4,
            """{"exitChannel":5}""").Reason, StringComparison.Ordinal);
        var missingPreBreak = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", compression, 4,
            """{"exitChannel":5}""", compression[4].CloseTimeUtc);
        Assert.True(missingPreBreak.IsBlocked);
        Assert.Contains("complete pre-break candles", missingPreBreak.Reason, StringComparison.Ordinal);
        var missingOpening = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", compression, 4,
            """{"exitChannel":5}""", compression[0].OpenTimeUtc.AddHours(-1));
        Assert.True(missingOpening.IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout",
            compression.Select((candle, index) => index == 59
                ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                    candle.CloseTimeUtc, 100m, 101m, 99m, 100m, candle.Volume, true, false)
                : candle).ToArray(), 4, """{"exitChannel":5}""",
            compression[57].CloseTimeUtc).ShouldExit);
    }

    [Fact]
    public void NewBollingerExitFreezesTheOpeningExcursionButOldPositionsKeepTheirMiddleBandRule()
    {
        var history = FlatExitHistory(100m, 99m, 101m,
            (56, 92m, 91m, 100m), (57, 94.6m, 92m, 96m),
            (58, 90m, 90m, 101m), (59, 102m, 92m, 103m));
        var settings = """{"bollingerPlanModel":"excursionMidBand"}""";

        var newPlan = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.bollinger-mean-reversion", history, 5, settings, history[57].CloseTimeUtc);
        var oldPlan = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.bollinger-mean-reversion", history, 4, settings, history[57].CloseTimeUtc);
        Assert.False(newPlan.ShouldExit);
        Assert.False(newPlan.IsBlocked);
        Assert.True(oldPlan.ShouldExit);
        Assert.Contains("No family-specific invalidation", newPlan.Reason, StringComparison.Ordinal);
        var broken = history.Select((candle, index) => index == 59
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                90m, 101m, 89m, 90m, candle.Volume, true, false) : candle).ToArray();
        var failure = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.bollinger-mean-reversion", broken, 5, settings, history[57].CloseTimeUtc);
        Assert.True(failure.ShouldExit);
        Assert.Contains("frozen Bollinger excursion/re-entry low 91", failure.Reason, StringComparison.Ordinal);
        var missingPreEntry = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.bollinger-mean-reversion", history, 5, settings, history[0].CloseTimeUtc);
        Assert.True(missingPreEntry.IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.bollinger-mean-reversion", history, 5, """{"bollingerPeriod":20}""",
            history[57].CloseTimeUtc).IsBlocked);
    }

    [Fact]
    public void NewRsiPullbackInvalidationPinsTheOpeningSwingAndLeavesVersionFourUnaffected()
    {
        var history = Enumerable.Range(0, 70).Select(index =>
        {
            var openTime = AsOfUtc.AddMinutes(index - 70);
            var close = index % 2 == 0 ? 100m : 101m;
            return new Candle("XBT/EUR", CandleInterval.OneMinute, openTime,
                openTime.AddMinutes(1), close, 102m, index == 55 ? 90m : 99m,
                close, 100m, true, false);
        }).ToArray();
        const string settings = """{"rsiPlanModel":"pullbackSwing","stopSwingLookback":5,"longExitRsi":80}""";
        var opening = history[58].CloseTimeUtc;

        var current = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.rsi-pullback", history, 5, settings, opening);
        Assert.False(current.IsBlocked);
        Assert.False(current.ShouldExit);
        var old = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.rsi-pullback", history, 4, settings, opening);
        Assert.False(old.IsBlocked);
        Assert.False(old.ShouldExit);
        var broken = history.Select((candle, index) => index == 69
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                89m, 102m, 89m, 89m, candle.Volume, true, false) : candle).ToArray();
        var failedSwing = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.rsi-pullback", broken, 5, settings, opening);
        Assert.True(failedSwing.ShouldExit);
        Assert.Contains("frozen RSI pullback swing low 90", failedSwing.Reason, StringComparison.Ordinal);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.rsi-pullback", history, 5, settings, history[1].CloseTimeUtc).IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.rsi-pullback", history, 5,
            """{"rsiPeriod":14}""", opening).IsBlocked);
        Assert.False(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.rsi-pullback", history, 4,
            """{"rsiPeriod":14}""", opening).IsBlocked);
    }

    [Fact]
    public void NewMacdInvalidationPinsOpeningSwingAndPreservesVersionFourBehavior()
    {
        var history = Enumerable.Range(0, 70).Select(index =>
        {
            var openTime = AsOfUtc.AddMinutes(index - 70);
            var close = index % 2 == 0 ? 100m : 101m;
            return new Candle("XBT/EUR", CandleInterval.OneMinute, openTime,
                openTime.AddMinutes(1), close, 102m, index == 55 ? 90m : 99m,
                close, 100m, true, false);
        }).ToArray();
        const string settings = """{"macdPlanModel":"crossSwing","stopSwingLookback":5}""";
        var opening = history[58].CloseTimeUtc;
        var current = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.macd-volume", history, 5, settings, opening);
        Assert.False(current.IsBlocked);
        var broken = history.Select((candle, index) => index == 69
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                89m, 102m, 89m, 89m, candle.Volume, true, false) : candle).ToArray();
        var failedSwing = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.macd-volume", broken, 5, settings, opening);
        Assert.True(failedSwing.ShouldExit);
        Assert.Contains("frozen MACD opening swing low 90", failedSwing.Reason, StringComparison.Ordinal);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.macd-volume", history, 5, settings, history[1].CloseTimeUtc).IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.macd-volume", history, 5, """{"macdSignal":9}""", opening).IsBlocked);
        var old = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.macd-volume", history, 4, """{"macdSignal":9}""", opening);
        Assert.False(old.IsBlocked);
        Assert.DoesNotContain("frozen", old.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NewEmaInvalidationPinsOpeningSwingAndPreservesVersionFourBehavior()
    {
        var history = Enumerable.Range(0, 70).Select(index =>
        {
            var openTime = AsOfUtc.AddMinutes(index - 70);
            var close = index % 2 == 0 ? 100m : 101m;
            return new Candle("XBT/EUR", CandleInterval.OneMinute, openTime,
                openTime.AddMinutes(1), close, 102m, index == 55 ? 90m : 99m,
                close, 100m, true, false);
        }).ToArray();
        const string settings = """{"emaPlanModel":"pullbackSwing","stopSwingLookback":5}""";
        var opening = history[58].CloseTimeUtc;
        var current = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", history, 5, settings, opening);
        Assert.False(current.IsBlocked);
        var broken = history.Select((candle, index) => index == 69
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                89m, 102m, 89m, 89m, candle.Volume, true, false) : candle).ToArray();
        var failedSwing = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", broken, 5, settings, opening);
        Assert.True(failedSwing.ShouldExit);
        Assert.Contains("frozen EMA pullback swing low 90", failedSwing.Reason, StringComparison.Ordinal);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", history, 5, settings, history[1].CloseTimeUtc).IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", history, 5,
            """{"signalInvalidationEma":50}""", opening).IsBlocked);
        var old = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", history, 4,
            """{"signalInvalidationEma":50}""", opening);
        Assert.False(old.IsBlocked);
        Assert.DoesNotContain("frozen", old.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CompressionExitRequiresReviewedProtectionOnlyForNewPositions()
    {
        var history = FlatExitHistory(100m, 99m, 101m,
            (40, 100m, 99m, 110m), (57, 105m, 99m, 106m),
            (58, 106m, 99m, 107m), (59, 105m, 99m, 106m));
        var opening = history[57].CloseTimeUtc;
        var reviewed = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", history, 5,
            """{"compressionPlanModel":"priorRange","exitChannel":5}""", opening);
        Assert.False(reviewed.IsBlocked);
        Assert.False(reviewed.ShouldExit);
        var unreviewed = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", history, 5,
            """{"exitChannel":5}""", opening);
        Assert.True(unreviewed.IsBlocked);
        var old = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout", history, 4,
            """{"exitChannel":5}""", opening);
        Assert.False(old.IsBlocked);
        Assert.False(old.ShouldExit);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.volatility-compression-breakout",
            history.Select((candle, index) => index == 59
                ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                    candle.CloseTimeUtc, 100m, 101m, 99m, 100m, candle.Volume, true, false)
                : candle).ToArray(), 5,
            """{"compressionPlanModel":"priorRange","exitChannel":5}""", opening).ShouldExit);
    }

    [Fact]
    public void MomentumExitUsesFrozenPreEntrySwingAndKeepsVersionThreeBehavior()
    {
        var history = Enumerable.Range(0, 230).Select(index =>
        {
            var open = AsOfUtc.AddDays(index - 230);
            var price = 100m + index * .1m;
            return new Candle("XBT/EUR", CandleInterval.OneDay, open, open.AddDays(1),
                price, price + 1m, index == 218 ? 95m : price - 1m,
                price, 20_000m, true, false);
        }).ToArray();
        var opening = history[225].CloseTimeUtc;
        const string settings = """{"momentumPlanModel":"dailySwing","stopSwingLookback":10}""";
        var safe = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.cross-sectional-momentum-rotation", history, 4, settings, opening);
        Assert.False(safe.IsBlocked);
        Assert.False(safe.ShouldExit);
        var broken = history.Select((candle, index) => index == 229
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc, candle.CloseTimeUtc,
                94m, candle.High, 94m, 94m, candle.Volume, true, false) : candle).ToArray();
        var failed = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.cross-sectional-momentum-rotation", broken, 4, settings, opening);
        Assert.True(failed.ShouldExit);
        Assert.Contains("frozen momentum swing low 95", failed.Reason, StringComparison.Ordinal);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.cross-sectional-momentum-rotation", history, 4,
            """{"stopSwingLookback":10}""", opening).IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.cross-sectional-momentum-rotation", history, 4, settings,
            history[1].CloseTimeUtc).IsBlocked);
        Assert.False(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.cross-sectional-momentum-rotation", history, 3,
            """{"stopSwingLookback":10}""", opening).IsBlocked);
    }

    [Fact]
    public void ThreeSwingExitPinsTheOpeningChannelAndRequiresReviewedSettings()
    {
        var (signal, _, _) = ThreeSwingChannelDivergenceModelTests.BullishEvidence();
        var opening = signal[^1].CloseTimeUtc;
        var later = new Candle("XBT/EUR", CandleInterval.FiveMinutes,
            opening, opening.AddMinutes(5), 130m, 131m, 129m, 130m, 1m, true, false);
        var series = signal.Append(later).ToArray();
        const string settings = """{"threeSwingPlanModel":"confirmedPivot","channelPeriod":50,"exitOnMacdReversal":false}""";
        var safe = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.three-swing-channel-divergence", series, 4, settings, opening);
        Assert.False(safe.IsBlocked);
        Assert.False(safe.ShouldExit);
        var broken = series.Select((candle, index) => index == series.Length - 1
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 77m, 78m, 76m, 77m, 1m, true, false)
            : candle).ToArray();
        var failed = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.three-swing-channel-divergence", broken, 4, settings, opening);
        Assert.True(failed.ShouldExit);
        Assert.Contains("frozen third-swing channel low", failed.Reason, StringComparison.Ordinal);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.three-swing-channel-divergence", series, 4,
            """{"channelPeriod":50}""", opening).IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.three-swing-channel-divergence", series, 4, settings,
            signal[1].CloseTimeUtc).IsBlocked);
        Assert.False(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.three-swing-channel-divergence", series, 3,
            """{"channelPeriod":50}""", opening).IsBlocked);
    }

    [Fact]
    public void EnsembleExitUsesFrozenFourHourSwingOnlyForReviewedVersionFive()
    {
        var history = Enumerable.Range(0, 230).Select(index =>
        {
            var open = AsOfUtc.AddHours((index - 230) * 4);
            var price = 100m + index * .1m;
            return new Candle("ETH/EUR", CandleInterval.FourHours, open, open.AddHours(4),
                price, price + 1m, price - 1m, price, 20_000m, true, false);
        }).ToArray();
        var opening = history[225].CloseTimeUtc;
        const string settings = """{"regimePlanModel":"fourHourSwing","stopSwingLookback":5}""";
        var safe = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.regime-switching-ensemble", history, 5, settings, opening);
        Assert.False(safe.IsBlocked);
        Assert.False(safe.ShouldExit);
        var broken = history.Select((candle, index) => index == 229
            ? new Candle(candle.Symbol, candle.Interval, candle.OpenTimeUtc,
                candle.CloseTimeUtc, 119m, candle.High, 118m, 119m,
                candle.Volume, true, false) : candle).ToArray();
        var failed = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.regime-switching-ensemble", broken, 5, settings, opening);
        Assert.True(failed.ShouldExit);
        Assert.Contains("frozen ensemble four-hour swing low", failed.Reason, StringComparison.Ordinal);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.regime-switching-ensemble", history, 5,
            """{"stopSwingLookback":5}""", opening).IsBlocked);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.regime-switching-ensemble", history, 5, settings,
            history[1].CloseTimeUtc).IsBlocked);
        Assert.False(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.regime-switching-ensemble", history, 4,
            """{"stopSwingLookback":5}""", opening).IsBlocked);
    }

    [Fact]
    public void NewIndicatorInvalidationsReadPinnedPeriodsAndFailClosedOnInvalidSavedSettings()
    {
        var falling = Falling(CandleInterval.FifteenMinutes).Candles;
        var ema = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", falling, 4,
            """{"signalInvalidationEma":30}""", falling[^2].CloseTimeUtc);
        var rsi = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.rsi-pullback", falling, 4,
            """{"rsiPeriod":10,"signalEma":30,"longExitRsi":75}""",
            falling[^2].CloseTimeUtc);
        var macd = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.macd-volume", falling, 4,
            """{"macdFast":5,"macdSlow":17,"macdSignal":6,"signalEma":30}""",
            falling[^2].CloseTimeUtc);
        Assert.True(ema.ShouldExit);
        Assert.Contains("EMA 30", ema.Reason, StringComparison.Ordinal);
        Assert.True(rsi.ShouldExit);
        Assert.Contains("RSI 10", rsi.Reason, StringComparison.Ordinal);
        Assert.Contains("target 75", rsi.Reason, StringComparison.Ordinal);
        Assert.True(macd.ShouldExit);
        Assert.Contains("MACD 5/17/6", macd.Reason, StringComparison.Ordinal);
        var invalid = ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", falling, 4,
            """{"signalInvalidationEma":10000}""");
        Assert.False(invalid.ShouldExit);
        Assert.Contains("require review", invalid.Reason, StringComparison.Ordinal);
        Assert.True(ApprovedStrategyInvalidationPolicy.Evaluate(
            "platform.ema-trend-continuation", falling, 3,
            """{"signalInvalidationEma":10000}""").ShouldExit);
    }

    [Theory]
    [InlineData("platform.ema-trend-continuation")]
    [InlineData("platform.donchian-breakout-ensemble")]
    [InlineData("platform.bollinger-mean-reversion")]
    [InlineData("platform.rsi-pullback")]
    [InlineData("platform.macd-volume")]
    [InlineData("platform.volatility-compression-breakout")]
    public void NewInvalidationVersionsRequireAttestedOpeningAndAClosedLaterSignal(string strategyId)
    {
        var falling = Falling(CandleInterval.FifteenMinutes).Candles;
        var missing = ApprovedStrategyInvalidationPolicy.Evaluate(strategyId, falling, 4, "{}");
        var opening = ApprovedStrategyInvalidationPolicy.Evaluate(
            strategyId, falling, 4, "{}", falling[^1].CloseTimeUtc);
        var lostOpening = ApprovedStrategyInvalidationPolicy.Evaluate(
            strategyId, falling, 4, "{}", falling[0].OpenTimeUtc.AddHours(-1));

        Assert.True(missing.IsBlocked);
        Assert.False(missing.ShouldExit);
        Assert.True(lostOpening.IsBlocked);
        Assert.False(lostOpening.ShouldExit);
        Assert.False(opening.IsBlocked);
        Assert.False(opening.ShouldExit);
        Assert.Contains("later closed signal candle", opening.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSavedHoldingLimitCannotSilentlyUseAPlatformDefault()
    {
        Assert.False(ApprovedStrategyHoldingPolicy.TryMaximumSignalCandles(
            "platform.ema-trend-continuation", """{"maximumHoldingCandles":10000}""",
            out _, out var error));
        Assert.Contains("Maximum holding", error, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            ApprovedStrategyHoldingPolicy.MaximumSignalCandles(
                "platform.ema-trend-continuation", """{"maximumHoldingCandles":10000}"""));
    }

    private static Candle[] FlatExitHistory(decimal close, decimal low, decimal high,
        params (int Index, decimal Close, decimal Low, decimal High)[] overrides)
    {
        var changes = overrides.ToDictionary(item => item.Index);
        return Enumerable.Range(0, 60).Select(index =>
        {
            var entry = changes.GetValueOrDefault(index);
            var open = AsOfUtc.AddMinutes(index - 60);
            return new Candle("XBT/EUR", CandleInterval.OneMinute, open, open.AddMinutes(1),
                changes.ContainsKey(index) ? entry.Close : close,
                changes.ContainsKey(index) ? entry.High : high,
                changes.ContainsKey(index) ? entry.Low : low,
                changes.ContainsKey(index) ? entry.Close : close,
                10m, true, false);
        }).ToArray();
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
