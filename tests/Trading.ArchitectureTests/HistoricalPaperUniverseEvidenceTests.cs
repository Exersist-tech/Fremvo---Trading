using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class HistoricalPaperUniverseEvidenceTests
{
    private static readonly DateTimeOffset s_close = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_boundary = s_close.AddHours(12);
    private static readonly DateTimeOffset s_imported = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void VerifiesEveryArchiveAgainstTheExactObservedDailyWindow()
    {
        var xbt = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var eth = Archive("ETH/EUR", Daily("ETH/EUR", 322, s_close));
        var snapshot = Snapshot([xbt, eth]);

        HistoricalPaperUniverseEvidence.Preflight(snapshot, [eth.Dataset, xbt.Dataset]);
        var windows = HistoricalPaperUniverseEvidence.Verify(snapshot, [eth, xbt]);

        Assert.Equal(2, windows.Count);
        Assert.Equal(s_close, windows["ETH/EUR"].AsOfUtc);
        Assert.Equal(xbt.Dataset.VersionIdentity, windows["XBT/EUR"].DatasetProvenance);
        Assert.Equal(ApprovedConsensusStrategyProfiles.RequiredHistory,
            windows["XBT/EUR"].Candles.Count);
    }

    [Fact]
    public void AValidLaterArchiveCandleCannotEnterAnEarlierUniverseDecision()
    {
        var original = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var snapshot = Snapshot([original]);
        var later = Daily("XBT/EUR", 323, s_close.AddDays(1));
        later[^1] = new Candle("XBT/EUR", CandleInterval.OneDay,
            later[^1].OpenTimeUtc, later[^1].CloseTimeUtc,
            10m, 12m, 9m, 11m, 1_001m, true, false);
        var revised = Archive("XBT/EUR", later);

        Assert.NotEqual(original.Dataset.VersionIdentity, revised.Dataset.VersionIdentity);
        Assert.Equal(HistoricalCandleFingerprint.Compute(
                HistoricalPaperUniverseEvidence.Verify(snapshot, [original])["XBT/EUR"].Candles),
            HistoricalCandleFingerprint.Compute(
                HistoricalPaperUniverseEvidence.Verify(snapshot, [revised])["XBT/EUR"].Candles));
    }

    [Fact]
    public void RefusesSubstitutedDailyContentMissingMembersAndUnrecordedHistory()
    {
        var xbt = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var eth = Archive("ETH/EUR", Daily("ETH/EUR", 322, s_close));
        var snapshot = Snapshot([xbt, eth]);
        var changed = xbt.Candles.ToArray();
        var last = changed[^1];
        changed[^1] = new Candle("XBT/EUR", CandleInterval.OneDay,
            last.OpenTimeUtc, last.CloseTimeUtc, last.Open, last.High, last.Low,
            last.Close, last.Volume + 1m, true, false);
        var substituted = Archive("XBT/EUR", changed);

        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.Verify(snapshot, [substituted, eth]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Verify(snapshot, [xbt]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Verify(snapshot, [xbt, xbt]));
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.Verify(Snapshot([xbt], includeDaily: false), [xbt]));
    }

    [Fact]
    public void RefusesArchivesLargerThanTheBoundBeforeInspectingTheirContents()
    {
        var xbt = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var oversized = xbt with { Candles = new Candle[100_001] };
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Verify(Snapshot([xbt]), [oversized]));
    }

    [Fact]
    public void PreflightRejectsMissingDuplicateOrIncompleteMemberManifestsBeforeBlobReads()
    {
        var xbt = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var eth = Archive("ETH/EUR", Daily("ETH/EUR", 322, s_close));
        var sol = Archive("SOL/EUR", Daily("SOL/EUR", 322, s_close));
        var snapshot = Snapshot([xbt, eth]);
        var wrongInterval = new HistoricalDataset("wrong-role", "trusted-test-copy",
            "ETH/EUR", "1H", eth.Dataset.FromUtc, eth.Dataset.ToUtc,
            322, eth.Dataset.ContentFingerprint, eth.Dataset.SourceVersion, s_imported);
        var shortRange = new HistoricalDataset("short-range", "trusted-test-copy",
            "ETH/EUR", "1D", s_close.AddDays(-319), s_close.AddDays(1),
            321, eth.Dataset.ContentFingerprint, eth.Dataset.SourceVersion, s_imported);
        var earlyEnd = new HistoricalDataset("early-end", "trusted-test-copy",
            "ETH/EUR", "1D", eth.Dataset.FromUtc, s_close.AddDays(-2),
            321, eth.Dataset.ContentFingerprint, eth.Dataset.SourceVersion, s_imported);
        var inconsistentCount = new HistoricalDataset("inconsistent-count", "trusted-test-copy",
            "ETH/EUR", "1D", eth.Dataset.FromUtc, eth.Dataset.ToUtc,
            321, eth.Dataset.ContentFingerprint, eth.Dataset.SourceVersion, s_imported);

        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot, [xbt.Dataset]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot, [xbt.Dataset, xbt.Dataset]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot, [xbt.Dataset, sol.Dataset]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot, [xbt.Dataset, wrongInterval]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot, [xbt.Dataset, shortRange]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot, [xbt.Dataset, earlyEnd]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot, [xbt.Dataset, inconsistentCount]));
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(Snapshot([xbt], includeDaily: false),
                [xbt.Dataset]));
    }

    [Fact]
    public void PreflightCapsAggregateManifestHistoryBeforeAnyPayloadIsLoaded()
    {
        var xbt = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var eth = Archive("ETH/EUR", Daily("ETH/EUR", 322, s_close));
        var sol = Archive("SOL/EUR", Daily("SOL/EUR", 322, s_close));
        var snapshot = Snapshot([xbt, eth, sol]);

        static HistoricalDataset LargeManifest(HistoricalPaperReplaySeries archive) =>
            new("long-run", "trusted-test-copy", archive.Dataset.Symbol,
                "1D", s_close.AddDays(-100_000), s_close.AddDays(-1),
                100_000, archive.Dataset.ContentFingerprint,
                archive.Dataset.SourceVersion, s_imported);

        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.Preflight(snapshot,
                [LargeManifest(xbt), LargeManifest(eth), sol.Dataset]));
    }

    [Fact]
    public void VerifiesTheExactRecordedHourlyWindowWithoutFutureLookAhead()
    {
        var original = Archive("XBT/EUR", Hourly("XBT/EUR", 322, s_boundary));
        var snapshot = ExpandedSnapshot(original);
        HistoricalPaperUniverseEvidence.PreflightSeries(snapshot, original.Dataset,
            "XBT/EUR", CandleInterval.OneHour);
        var result = HistoricalPaperUniverseEvidence.VerifySeries(snapshot,
            original, "XBT/EUR", CandleInterval.OneHour);
        var later = Hourly("XBT/EUR", 323, s_boundary.AddHours(1));
        var last = later[^1];
        later[^1] = new Candle("XBT/EUR", CandleInterval.OneHour,
            last.OpenTimeUtc, last.CloseTimeUtc, 10m, 12m, 9m, 11m,
            1_001m, true, false);
        var changedAfterScan = Archive("XBT/EUR", later);
        var futureExcluded = HistoricalPaperUniverseEvidence.VerifySeries(snapshot,
            changedAfterScan, "XBT/EUR", CandleInterval.OneHour);

        Assert.Equal(s_boundary, result.AsOfUtc);
        Assert.Equal(original.Dataset.VersionIdentity, result.DatasetProvenance);
        Assert.NotEqual(original.Dataset.VersionIdentity, changedAfterScan.Dataset.VersionIdentity);
        Assert.Equal(HistoricalCandleFingerprint.Compute(result.Candles),
            HistoricalCandleFingerprint.Compute(futureExcluded.Candles));
    }

    [Fact]
    public void RefusesUnrecordedOrSubstitutedTimeframeInputsBeforeClaimingParity()
    {
        var hourly = Archive("XBT/EUR", Hourly("XBT/EUR", 322, s_boundary));
        var snapshot = ExpandedSnapshot(hourly);
        var priorSchema = Snapshot([Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close))]);
        var modified = hourly.Candles.ToArray();
        var last = modified[^1];
        modified[^1] = new Candle(last.Symbol, last.Interval,
            last.OpenTimeUtc, last.CloseTimeUtc, last.Open, last.High, last.Low,
            last.Close, last.Volume + 1m, true, false);
        var substituted = Archive("XBT/EUR", modified);
        var insufficient = Archive("XBT/EUR", Hourly("XBT/EUR", 319, s_boundary));
        var missingClose = Archive("XBT/EUR", Hourly("XBT/EUR", 320, s_boundary.AddHours(-1)));
        var daily = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));

        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.PreflightSeries(priorSchema,
                hourly.Dataset, "XBT/EUR", CandleInterval.OneHour));
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.PreflightSeries(
                ExpandedSnapshot(), hourly.Dataset, "XBT/EUR", CandleInterval.OneHour));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.PreflightSeries(snapshot,
                daily.Dataset, "XBT/EUR", CandleInterval.OneHour));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.PreflightSeries(snapshot,
                insufficient.Dataset, "XBT/EUR", CandleInterval.OneHour));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.PreflightSeries(snapshot,
                missingClose.Dataset, "XBT/EUR", CandleInterval.OneHour));
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.VerifySeries(snapshot, substituted,
                "XBT/EUR", CandleInterval.OneHour));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.VerifySeries(snapshot,
                hourly with { Candles = modified }, "XBT/EUR", CandleInterval.OneHour));
    }

    [Fact]
    public void MatchesEveryRecordedRoleFromUnorderedCompleteArchiveEvidence()
    {
        var daily = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var hourly = Archive("XBT/EUR", Hourly("XBT/EUR", 322, s_boundary));
        var snapshot = ExpandedSnapshot(hourly, daily);
        HistoricalPaperUniverseEvidence.PreflightContext(snapshot,
            [hourly.Dataset, daily.Dataset]);

        var matched = HistoricalPaperUniverseEvidence.VerifyContext(
            snapshot, [hourly, daily]);

        Assert.Equal(2, matched.Count);
        Assert.Equal(s_close, matched[("XBT/EUR", CandleInterval.OneDay)].AsOfUtc);
        Assert.Equal(s_boundary, matched[("XBT/EUR", CandleInterval.OneHour)].AsOfUtc);
        Assert.Equal(hourly.Dataset.VersionIdentity,
            matched[("XBT/EUR", CandleInterval.OneHour)].DatasetProvenance);
    }

    [Fact]
    public void CompleteContextPreflightRejectsMissingDuplicateOrExcessiveArchiveRoles()
    {
        var daily = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var hourly = Archive("XBT/EUR", Hourly("XBT/EUR", 322, s_boundary));
        var fourHourly = Archive("XBT/EUR", FourHourly("XBT/EUR", 322, s_boundary));
        var snapshot = ExpandedSnapshot(daily, hourly, fourHourly);

        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.PreflightContext(snapshot,
                [daily.Dataset, hourly.Dataset]));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.PreflightContext(snapshot,
                [daily.Dataset, hourly.Dataset, hourly.Dataset]));
        var extra = Archive("ETH/EUR", Hourly("ETH/EUR", 322, s_boundary));
        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.PreflightContext(snapshot,
                [daily.Dataset, hourly.Dataset, extra.Dataset]));

        static HistoricalDataset Large(HistoricalPaperReplaySeries archive,
            DateTimeOffset expectedClose)
        {
            var duration = TimeSpan.FromMinutes((int)archive.Candles[0].Interval);
            return new HistoricalDataset("long-run", archive.Dataset.Source,
                archive.Dataset.Symbol, archive.Dataset.Interval,
                expectedClose.AddTicks(-duration.Ticks * 100_000),
                expectedClose.AddTicks(-duration.Ticks),
                100_000, archive.Dataset.ContentFingerprint,
                archive.Dataset.SourceVersion, s_imported);
        }

        Assert.Throws<ArgumentException>(() =>
            HistoricalPaperUniverseEvidence.PreflightContext(snapshot,
                [Large(daily, s_close), Large(hourly, s_boundary), fourHourly.Dataset]));
        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.PreflightContext(Snapshot([daily]),
                [daily.Dataset]));
    }

    [Fact]
    public void CompleteContextCannotHideAChangedRecordedRole()
    {
        var daily = Archive("XBT/EUR", Daily("XBT/EUR", 322, s_close));
        var hourly = Archive("XBT/EUR", Hourly("XBT/EUR", 322, s_boundary));
        var snapshot = ExpandedSnapshot(daily, hourly);
        var tampered = hourly.Candles.ToArray();
        var last = tampered[^1];
        tampered[^1] = new Candle(last.Symbol, last.Interval, last.OpenTimeUtc,
            last.CloseTimeUtc, last.Open, last.High, last.Low, last.Close,
            last.Volume + 1m, true, false);

        Assert.Throws<InvalidOperationException>(() =>
            HistoricalPaperUniverseEvidence.VerifyContext(snapshot,
                [daily, Archive("XBT/EUR", tampered)]));
    }

    private static PaperScanUniverseSnapshot ExpandedSnapshot(
        params HistoricalPaperReplaySeries[] archives)
    {
        const string family = "platform.ema-trend-continuation";
        Assert.True(ApprovedStrategyParameters.TryNormalize(
            family, "{}", out var normalized, out var error), error);
        var members = new[]
        {
            new PaperTrainingUniverseCandidate("XBT/EUR",
                5_000_000m, 1_500_000m, 30,
                new PaperExchangeFilters(.01m, .001m, .001m, 10m))
        };
        var windows = archives.ToDictionary(
            archive => (archive.Dataset.Symbol, archive.Candles[0].Interval),
            archive =>
            {
                var interval = archive.Candles[0].Interval;
                var duration = TimeSpan.FromMinutes((int)interval);
                var asOf = new DateTimeOffset(
                    s_boundary.UtcTicks - s_boundary.UtcTicks % duration.Ticks,
                    TimeSpan.Zero);
                return new ExperimentCandleSeries(archive.Dataset.Symbol, interval, asOf,
                    archive.Candles.Where(candle => candle.CloseTimeUtc <= asOf)
                        .TakeLast(ApprovedConsensusStrategyProfiles.RequiredHistory).ToArray());
            });
        return PaperScanUniverseSnapshot.Capture(Guid.NewGuid(), s_boundary,
            s_boundary.AddSeconds(30), members, PaperTrainingUniversePolicy.PlatformDefault,
            windows, [new PaperScanStrategyEvidence(family, 5, normalized, true)]);
    }

    private static PaperScanUniverseSnapshot Snapshot(
        IReadOnlyList<HistoricalPaperReplaySeries> archives,
        bool includeDaily = true)
    {
        var members = archives.Select((archive, index) =>
            new PaperTrainingUniverseCandidate(archive.Dataset.Symbol,
                5_000_000m - index * 1_000_000m, 1_500_000m, 30,
                new PaperExchangeFilters(.01m, .001m, .001m, 10m))).ToArray();
        var daily = new Dictionary<(string, CandleInterval), ExperimentCandleSeries>();
        if (includeDaily)
            foreach (var archive in archives)
            {
                daily.Add((archive.Dataset.Symbol, CandleInterval.OneDay),
                    new ExperimentCandleSeries(archive.Dataset.Symbol,
                        CandleInterval.OneDay, s_close,
                        archive.Candles.Where(item => item.CloseTimeUtc <= s_close)
                            .TakeLast(ApprovedConsensusStrategyProfiles.RequiredHistory).ToArray()));
            }
        return PaperScanUniverseSnapshot.Capture(Guid.NewGuid(), s_boundary,
            s_boundary.AddSeconds(30), members, PaperTrainingUniversePolicy.PlatformDefault, daily);
    }

    private static HistoricalPaperReplaySeries Archive(string symbol, Candle[] candles)
    {
        var fingerprint = HistoricalCandleFingerprint.Compute(candles);
        var dataset = new HistoricalDataset($"archive-{fingerprint}",
            "trusted-test-copy", symbol, candles[0].Interval switch
            {
                CandleInterval.OneDay => "1D",
                CandleInterval.OneHour => "1H",
                CandleInterval.FourHours => "4H",
                _ => throw new ArgumentOutOfRangeException(nameof(candles))
            },
            candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc,
            candles.Length, fingerprint, new string('A', 64), s_imported);
        return new(dataset, candles);
    }

    private static Candle[] Daily(string symbol, int count, DateTimeOffset lastClose) =>
        Enumerable.Range(0, count)
            .Select(index =>
            {
                var open = lastClose.AddDays(index - count);
                return new Candle(symbol, CandleInterval.OneDay, open, open.AddDays(1),
                    10m, 11m, 9m, 10m, 1_000m, true, false);
            }).ToArray();

    private static Candle[] Hourly(string symbol, int count, DateTimeOffset lastClose) =>
        Enumerable.Range(0, count)
            .Select(index =>
            {
                var open = lastClose.AddHours(index - count);
                return new Candle(symbol, CandleInterval.OneHour, open, open.AddHours(1),
                    10m, 11m, 9m, 10m, 1_000m, true, false);
            }).ToArray();

    private static Candle[] FourHourly(string symbol, int count, DateTimeOffset lastClose) =>
        Enumerable.Range(0, count)
            .Select(index =>
            {
                var open = lastClose.AddHours((index - count) * 4);
                return new Candle(symbol, CandleInterval.FourHours, open, open.AddHours(4),
                    10m, 11m, 9m, 10m, 1_000m, true, false);
            }).ToArray();
}
