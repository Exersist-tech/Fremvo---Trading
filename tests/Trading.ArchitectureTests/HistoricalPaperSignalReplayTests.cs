using System.Text.Json;
using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.ArchitectureTests;

public sealed class HistoricalPaperSignalReplayTests
{
    private static readonly DateTimeOffset s_end = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_imported = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Symbol = "XBT/EUR";

    public static IEnumerable<object[]> DirectProfiles()
    {
        string[] families =
        [
            "platform.ema-trend-continuation",
            "platform.donchian-breakout-ensemble",
            "platform.bollinger-mean-reversion",
            "platform.rsi-pullback",
            "platform.macd-volume",
            "platform.volatility-compression-breakout",
            "platform.session-conditioned-breakout",
            "platform.three-swing-channel-divergence"
        ];
        return families.SelectMany(family => ApprovedConsensusStrategyProfiles.For(family)
            .Select(profile => new object[] { family, profile.Signal }));
    }

    [Theory]
    [MemberData(nameof(DirectProfiles))]
    public void ReplaysTheExactScannerCandidateForEachApprovedDirectProfile(
        string family, CandleInterval signalInterval)
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request(family, strategies, signalInterval);
        var result = new HistoricalPaperSignalReplay(strategies).Run(request);
        var definition = strategies.ResolveDefinition(family, request.StrategyVersion);
        var profile = ApprovedConsensusStrategyProfiles.Resolve(family, request.SignalInterval);

        Assert.Equal([request.FromSignalCloseUtc, request.ToSignalCloseUtc],
            result.Observations.Select(observation => observation.SignalCloseUtc));
        Assert.Equal(request.StrategyVersion, result.StrategyVersion);
        Assert.Equal(request.Series.Count, result.DatasetVersions.Count);
        Assert.Equal(request.Series.Count, result.ArchiveManifests.Count);
        Assert.All(result.ArchiveManifests, item =>
        {
            Assert.Equal(result.DatasetVersions[item.Key], item.Value.VersionIdentity);
            Assert.Equal("test-offline-ohlcvt", item.Value.Source);
            Assert.Equal(s_imported, item.Value.CreatedAtUtc);
        });
        foreach (var observed in result.Observations)
        {
            var series = request.Series.ToDictionary(
                item => item.Candles[0].Interval,
                item =>
                {
                    var duration = TimeSpan.FromMinutes((int)item.Candles[0].Interval);
                    var asOf = new DateTimeOffset(
                        observed.SignalCloseUtc.UtcTicks - observed.SignalCloseUtc.UtcTicks % duration.Ticks,
                        TimeSpan.Zero);
                    var window = item.Candles
                        .Where(candle => candle.CloseTimeUtc <= asOf)
                        .TakeLast(ApprovedConsensusStrategyProfiles.RequiredHistory)
                        .ToArray();
                    Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory, window.Length);
                    Assert.Equal(asOf, window[^1].CloseTimeUtc);
                    return new ExperimentCandleSeries(Symbol, item.Candles[0].Interval, asOf, window);
                });
            var expected = ContinuousPaperOpportunityScanner.EvaluateDirectCandidate(
                strategies, definition, series[profile.Regime], series[profile.Signal],
                series[profile.Execution], result.NormalizedParametersJson,
                family == "platform.three-swing-channel-divergence"
                    ? series[CandleInterval.OneHour]
                    : null);
            Assert.Equal(expected.Outcome, observed.Analysis.Outcome);
            Assert.Equal(expected.Value, observed.Analysis.Value);
            Assert.Equal(expected.Reason, observed.Analysis.Reason);
            Assert.Equal(expected.Consensus?.Checks, observed.Analysis.Consensus?.Checks);
            Assert.DoesNotContain("closed warm-up", observed.Analysis.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AChangedFutureCandleCannotChangeAnEarlierDecision()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request("platform.ema-trend-continuation", strategies);
        var replay = new HistoricalPaperSignalReplay(strategies);
        var original = replay.Run(request);
        var signal = request.Series.Single(item =>
            item.Candles[0].Interval == request.SignalInterval);
        var altered = signal.Candles.ToArray();
        var last = altered[^1];
        altered[^1] = new Candle(Symbol, request.SignalInterval, last.OpenTimeUtc,
            last.CloseTimeUtc, last.Open, 10_000m, last.Low, 9_999m, last.Volume,
            true, false);
        var changed = request with
        {
            Series = request.Series.Select(item => item == signal
                ? Archive(request.SignalInterval, altered)
                : item).ToArray()
        };

        var replayed = replay.Run(changed);

        Assert.NotEqual(original.DatasetVersions[request.SignalInterval],
            replayed.DatasetVersions[request.SignalInterval]);
        Assert.Equal(original.Observations[0].Analysis.Outcome,
            replayed.Observations[0].Analysis.Outcome);
        Assert.Equal(original.Observations[0].Analysis.Reason,
            replayed.Observations[0].Analysis.Reason);
        Assert.Equal(original.Observations[0].Analysis.Consensus?.Checks,
            replayed.Observations[0].Analysis.Consensus?.Checks);
        Assert.NotEqual(original.Observations[^1].Analysis.Reason,
            replayed.Observations[^1].Analysis.Reason);
    }

    [Fact]
    public void RefusesARequestWithMissingOrDuplicatedTimeframeRoles()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request("platform.ema-trend-continuation", strategies);
        var replay = new HistoricalPaperSignalReplay(strategies);

        Assert.Throws<ArgumentException>(() => replay.Run(
            request with { Series = request.Series.Skip(1).ToArray() }));
        Assert.Throws<ArgumentException>(() => replay.Run(
            request with { Series = [.. request.Series.Skip(1), request.Series[1]] }));
    }

    [Fact]
    public void RefusesMisalignedOrOversizedClosedBoundaryRequests()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request("platform.ema-trend-continuation", strategies);
        var replay = new HistoricalPaperSignalReplay(strategies);

        Assert.Throws<ArgumentOutOfRangeException>(() => replay.Run(
            request with { FromSignalCloseUtc = request.FromSignalCloseUtc.AddMinutes(1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => replay.Run(
            request with { FromSignalCloseUtc = request.FromSignalCloseUtc.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => replay.Run(
            request with { ToSignalCloseUtc = request.FromSignalCloseUtc.AddMinutes(5 * 2_000) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => replay.Run(
            request with { ToSignalCloseUtc = request.ToSignalCloseUtc.AddMinutes(5) }));
    }

    [Fact]
    public void InsufficientHigherTimeframeWarmupRecordsABlockRatherThanDroppingTheBoundary()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request("platform.ema-trend-continuation", strategies);
        var shortened = request.Series.Select(item =>
            item.Candles[0].Interval == CandleInterval.OneHour
                ? Archive(CandleInterval.OneHour, item.Candles.TakeLast(320).ToArray())
                : item).ToArray();

        var result = new HistoricalPaperSignalReplay(strategies)
            .Run(request with { Series = shortened });

        Assert.Equal(2, result.Observations.Count);
        Assert.Equal(ExperimentAnalysisOutcome.Blocked, result.Observations[0].Analysis.Outcome);
        Assert.Contains("warm-up", result.Observations[0].Analysis.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesCorruptArchiveBeforeAnyEvaluation()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request("platform.ema-trend-continuation", strategies);
        var signal = request.Series.Single(item =>
            item.Candles[0].Interval == request.SignalInterval);
        var tampered = signal.Candles.ToArray();
        var last = tampered[^1];
        tampered[^1] = new Candle(Symbol, request.SignalInterval, last.OpenTimeUtc,
            last.CloseTimeUtc, 0m, last.High, last.Low, last.Close, last.Volume,
            true, false);

        Assert.Throws<ArgumentException>(() => new HistoricalPaperSignalReplay(strategies)
            .Run(request with
            {
                Series = request.Series.Select(item => item == signal
                    ? item with { Candles = tampered }
                    : item).ToArray()
            }));
    }

    [Fact]
    public void PreservesTheScannerPlanVetoForUnreviewedSavedSettings()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request("platform.donchian-breakout-ensemble", strategies)
            with { ParametersJson = """{"minimumAgreement":4}""" };

        var result = new HistoricalPaperSignalReplay(strategies).Run(request);

        Assert.All(result.Observations, item =>
        {
            Assert.Equal(ExperimentAnalysisOutcome.Blocked, item.Analysis.Outcome);
            Assert.True(item.Analysis.Consensus?.MandatoryVeto);
            Assert.Contains("owner review", item.Analysis.Reason, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("platform.cross-sectional-momentum-rotation")]
    [InlineData("platform.relative-strength-pullback-rotation")]
    [InlineData("platform.regime-switching-ensemble")]
    public void RefusesToInventHistoricalEligibleUniverseMembership(string family)
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var request = Request("platform.ema-trend-continuation", strategies);
        var version = strategies.Definitions.Single(item => item.FamilyId == family).Version;

        Assert.Throws<InvalidOperationException>(() => new HistoricalPaperSignalReplay(strategies)
            .Run(request with { FamilyId = family, StrategyVersion = version }));
    }

    [Fact]
    public void ReplayOutputIdentifiesItsScopeAndPinnedEvidence()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var result = new HistoricalPaperSignalReplay(strategies).Run(
            Request("platform.ema-trend-continuation", strategies));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));

        Assert.Equal("closed-candle-signal-only",
            json.RootElement.GetProperty("EvidenceScope").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("DatasetVersions")
            .EnumerateObject().Count());
        Assert.Equal("test-offline-ohlcvt",
            json.RootElement.GetProperty("ArchiveManifests").GetProperty("OneHour")
                .GetProperty("Source").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("Observations")
            .GetArrayLength());
        Assert.True(json.RootElement.GetProperty("Observations")[0]
            .GetProperty("Analysis").GetProperty("Consensus")
            .TryGetProperty("Checks", out _));
    }

    private static HistoricalPaperSignalReplayRequest Request(
        string family, ApprovedExperimentStrategyRegistry strategies,
        CandleInterval? signalInterval = null)
    {
        var profile = signalInterval is null
            ? ApprovedConsensusStrategyProfiles.For(family)[0]
            : ApprovedConsensusStrategyProfiles.Resolve(family, signalInterval.Value);
        var version = strategies.Definitions.Single(item => item.FamilyId == family).Version;
        var evidence = ApprovedConsensusStrategyProfiles.RequiredIntervals(family, profile.Signal)
            .Select(interval => Archive(interval, Candles(interval, 330)))
            .ToArray();
        var duration = TimeSpan.FromMinutes((int)profile.Signal);
        return new HistoricalPaperSignalReplayRequest(
            family, version, Symbol, profile.Signal, "{}",
            s_end - duration, s_end, evidence);
    }

    private static Candle[] Candles(CandleInterval interval, int count)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var first = s_end - TimeSpan.FromTicks(duration.Ticks * count);
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var open = 100m + index / 10m;
                var at = first + TimeSpan.FromTicks(duration.Ticks * index);
                return new Candle(Symbol, interval, at, at + duration,
                    open, open + 2m, open - 2m, open + 1m, 1_000m, true, false);
            }).ToArray();
    }

    private static HistoricalPaperReplaySeries Archive(
        CandleInterval interval, Candle[] candles)
    {
        var fingerprint = HistoricalCandleFingerprint.Compute(candles);
        var dataset = new HistoricalDataset(
            $"archive-{fingerprint}", "test-offline-ohlcvt", Symbol,
            interval switch
            {
                CandleInterval.OneMinute => "1M",
                CandleInterval.FiveMinutes => "5M",
                CandleInterval.FifteenMinutes => "15M",
                CandleInterval.OneHour => "1H",
                CandleInterval.FourHours => "4H",
                CandleInterval.OneDay => "1D",
                _ => throw new ArgumentOutOfRangeException(nameof(interval))
            },
            candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc, candles.Length,
            fingerprint, new string('A', 64), s_imported);
        return new HistoricalPaperReplaySeries(dataset, candles);
    }
}
