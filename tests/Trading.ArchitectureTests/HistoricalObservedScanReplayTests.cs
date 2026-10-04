using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class HistoricalObservedScanReplayTests
{
    private static readonly DateTimeOffset s_imported = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<string> s_symbols =
        Array.AsReadOnly(["XBT/EUR", "ETH/EUR"]);
    private static readonly IReadOnlyList<CandleInterval> s_intervals =
        Array.AsReadOnly([CandleInterval.OneDay, CandleInterval.FourHours, CandleInterval.OneHour]);

    [Theory]
    [InlineData("platform.cross-sectional-momentum-rotation", 0)]
    [InlineData("platform.relative-strength-pullback-rotation", 13)]
    [InlineData("platform.regime-switching-ensemble", 12)]
    public void ReconstructsTheExactScannerEvaluatorForEachUniverseFamily(string family, int hour)
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var (snapshot, archives) = Scenario(strategies, hour);
        var replay = new HistoricalObservedScanReplay(strategies);
        var result = replay.Run(new(snapshot, archives, family, "ETH/EUR"));
        var windows = HistoricalPaperUniverseEvidence.VerifyContext(snapshot, archives);
        var saved = snapshot.StrategyEvidence!.Single(item => item.FamilyId == family);
        var profile = ApprovedConsensusStrategyProfiles.For(family).Single();
        var expected = ContinuousPaperOpportunityScanner.EvaluateCandidate(
            strategies, strategies.ResolveDefinition(family, saved.Version),
            snapshot.Members.Single(item => item.Symbol == "ETH/EUR"),
            windows[("ETH/EUR", profile.Regime)],
            windows[("ETH/EUR", profile.Signal)],
            windows[("ETH/EUR", profile.Execution)],
            snapshot.Members, windows, saved.ParametersJson,
            snapshot.StrategyEvidence!.ToDictionary(item => item.FamilyId,
                item => item.ParametersJson, StringComparer.Ordinal));

        Assert.Equal("observed-boundary-scanner-signal-only", result.EvidenceScope);
        Assert.Equal(saved.Version, result.StrategyVersion);
        Assert.Equal(snapshot.Fingerprint, result.SnapshotFingerprint);
        Assert.Equal(snapshot.SeriesEvidence!.Count, result.MatchedInputs.Count);
        Assert.Equal(expected.Outcome, result.Analysis.Outcome);
        Assert.Equal(expected.Value, result.Analysis.Value);
        Assert.Equal(expected.Reason, result.Analysis.Reason);
        Assert.Equal(expected.Consensus?.Checks, result.Analysis.Consensus?.Checks);
        Assert.Equal(expected.SelectedComponent, result.Analysis.SelectedComponent);
    }

    [Fact]
    public void RefusesUnobservedFamilyOrRequiredRolesWithoutInventingAnEvaluation()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var (snapshot, archives) = Scenario(strategies, 13);
        var replay = new HistoricalObservedScanReplay(strategies);
        var family = "platform.relative-strength-pullback-rotation";

        Assert.Throws<ArgumentException>(() => replay.Run(
            new(snapshot, archives, family, "SOL/EUR")));
        Assert.Throws<ArgumentException>(() => replay.Run(
            new(snapshot, archives, "platform.rsi-pullback", "ETH/EUR")));
        var notEvaluated = Capture(strategies, snapshot.SignalBoundaryUtc, archives,
            familyToDisable: family);
        Assert.Throws<InvalidOperationException>(() => replay.Run(
            new(notEvaluated, archives, family, "ETH/EUR")));
        var missingExecution = archives.Where(item =>
            !(item.Dataset.Symbol == "ETH/EUR" && item.Dataset.Interval == "1H")).ToArray();
        var missingSnapshot = Capture(strategies, snapshot.SignalBoundaryUtc, missingExecution);
        Assert.Throws<InvalidOperationException>(() => replay.Run(
            new(missingSnapshot, missingExecution, family, "ETH/EUR")));
        var wrongBoundary = Scenario(strategies, 12);
        Assert.Throws<InvalidOperationException>(() => replay.Run(
            new(wrongBoundary.Snapshot, wrongBoundary.Archives, family, "ETH/EUR")));
    }

    [Fact]
    public void RefusesVersionDriftInvalidParametersAndSubstitutedArchiveContent()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var (snapshot, archives) = Scenario(strategies, 0);
        var replay = new HistoricalObservedScanReplay(strategies);
        const string family = "platform.cross-sectional-momentum-rotation";
        var changedVersion = Capture(strategies, snapshot.SignalBoundaryUtc, archives,
            familyToDowngrade: family);
        Assert.Throws<InvalidOperationException>(() => replay.Run(
            new(changedVersion, archives, family, "ETH/EUR")));
        var invalidSettings = Capture(strategies, snapshot.SignalBoundaryUtc, archives,
            familyToInvalidate: family);
        Assert.Throws<InvalidOperationException>(() => replay.Run(
            new(invalidSettings, archives, family, "ETH/EUR")));
        var chosen = archives.Single(item =>
            item.Dataset.Symbol == "XBT/EUR" && item.Dataset.Interval == "1D");
        var changed = chosen.Candles.ToArray();
        var last = changed[^1];
        changed[^1] = new Candle(last.Symbol, last.Interval,
            last.OpenTimeUtc, last.CloseTimeUtc, last.Open, last.High,
            last.Low, last.Close, last.Volume + 1m, true, false);
        var substituted = Archive("XBT/EUR", CandleInterval.OneDay, changed);
        Assert.Throws<InvalidOperationException>(() => replay.Run(
            new(snapshot, archives.Select(item => item == chosen ? substituted : item).ToArray(),
                family, "ETH/EUR")));
    }

    [Fact]
    public void AChangedNextDayCannotAlterAnEarlierObservedRotationDecision()
    {
        var strategies = ApprovedExperimentStrategyRegistry.CreatePlatformDefault();
        var (snapshot, archives) = Scenario(strategies, 0);
        var replay = new HistoricalObservedScanReplay(strategies);
        const string family = "platform.cross-sectional-momentum-rotation";
        var original = replay.Run(new(snapshot, archives, family, "ETH/EUR"));
        var chosen = archives.Single(item =>
            item.Dataset.Symbol == "XBT/EUR" && item.Dataset.Interval == "1D");
        var previous = chosen.Candles[^1];
        var later = new Candle("XBT/EUR", CandleInterval.OneDay,
            previous.CloseTimeUtc, previous.CloseTimeUtc.AddDays(1),
            20m, 26m, 19m, 25m, 1_000m, true, false);
        var extended = Archive("XBT/EUR", CandleInterval.OneDay,
            [.. chosen.Candles, later]);
        var replayed = replay.Run(new(snapshot,
            archives.Select(item => item == chosen ? extended : item).ToArray(),
            family, "ETH/EUR"));

        Assert.NotEqual(chosen.Dataset.VersionIdentity, extended.Dataset.VersionIdentity);
        Assert.Equal(original.Analysis.Reason, replayed.Analysis.Reason);
        Assert.Equal(original.Analysis.Consensus?.Checks, replayed.Analysis.Consensus?.Checks);
        Assert.Equal(original.MatchedInputs.Single(item =>
                item.Symbol == "XBT/EUR" && item.Interval == CandleInterval.OneDay).WindowFingerprint,
            replayed.MatchedInputs.Single(item =>
                item.Symbol == "XBT/EUR" && item.Interval == CandleInterval.OneDay).WindowFingerprint);
    }

    private static (PaperScanUniverseSnapshot Snapshot, HistoricalPaperReplaySeries[] Archives)
        Scenario(ApprovedExperimentStrategyRegistry strategies, int hour)
    {
        var boundary = new DateTimeOffset(2026, 8, 1, hour, 0, 0, TimeSpan.Zero);
        var archives = s_symbols
            .SelectMany(symbol => s_intervals.Select(interval =>
            {
                var duration = TimeSpan.FromMinutes((int)interval);
                var close = new DateTimeOffset(
                    boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
                var candles = Enumerable.Range(0, 322)
                    .Select(index =>
                    {
                        var open = close.AddTicks((index - 322) * duration.Ticks);
                        var price = 10m + index / 100m + (symbol == "ETH/EUR" ? 1m : 0m);
                        return new Candle(symbol, interval, open, open.Add(duration),
                            price, price + 1m, price - 1m, price + .2m,
                            1_000m, true, false);
                    }).ToArray();
                return Archive(symbol, interval, candles);
            })).ToArray();
        return (Capture(strategies, boundary, archives), archives);
    }

    private static PaperScanUniverseSnapshot Capture(
        ApprovedExperimentStrategyRegistry strategies,
        DateTimeOffset boundary,
        IReadOnlyList<HistoricalPaperReplaySeries> archives,
        string? familyToDisable = null,
        string? familyToDowngrade = null,
        string? familyToInvalidate = null)
    {
        PaperTrainingUniverseCandidate[] members =
        [
            new("XBT/EUR", 5_000_000m, 1_500_000m, 30,
                new PaperExchangeFilters(.01m, .001m, .001m, 10m)),
            new("ETH/EUR", 4_000_000m, 1_500_000m, 30,
                new PaperExchangeFilters(.01m, .001m, .001m, 10m))
        ];
        var series = archives.ToDictionary(
            item => (item.Dataset.Symbol, item.Candles[0].Interval),
            item =>
            {
                var duration = TimeSpan.FromMinutes((int)item.Candles[0].Interval);
                var asOf = new DateTimeOffset(
                    boundary.UtcTicks - boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
                return new ExperimentCandleSeries(item.Dataset.Symbol,
                    item.Candles[0].Interval, asOf,
                    item.Candles.Where(candle => candle.CloseTimeUtc <= asOf)
                        .TakeLast(ApprovedConsensusStrategyProfiles.RequiredHistory).ToArray());
            });
        var saved = strategies.Definitions.OrderBy(item => item.FamilyId, StringComparer.Ordinal)
            .Select(item =>
            {
                Assert.True(ApprovedStrategyParameters.TryNormalize(
                    item.FamilyId, "{}", out var parameters, out var error), error);
                return new PaperScanStrategyEvidence(item.FamilyId,
                    item.Version - (item.FamilyId == familyToDowngrade ? 1 : 0),
                    item.FamilyId == familyToInvalidate ? string.Empty : parameters,
                    item.FamilyId != familyToDisable,
                    item.FamilyId != familyToInvalidate);
            }).ToArray();
        return PaperScanUniverseSnapshot.Capture(Guid.NewGuid(), boundary,
            boundary.AddSeconds(30), members, PaperTrainingUniversePolicy.PlatformDefault,
            series, saved);
    }

    private static HistoricalPaperReplaySeries Archive(
        string symbol, CandleInterval interval, Candle[] candles)
    {
        var fingerprint = HistoricalCandleFingerprint.Compute(candles);
        var code = interval switch
        {
            CandleInterval.OneDay => "1D",
            CandleInterval.FourHours => "4H",
            CandleInterval.OneHour => "1H",
            _ => throw new ArgumentOutOfRangeException(nameof(interval))
        };
        return new(new HistoricalDataset($"archive-{fingerprint}", "trusted-test-copy",
            symbol, code, candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc,
            candles.Length, fingerprint, new string('A', 64), s_imported), candles);
    }
}
