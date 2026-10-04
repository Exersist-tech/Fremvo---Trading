using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Trading.Application.Experiments;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.MarketData;
using Trading.MarketData.Experiments;
using Trading.Risk;

namespace Trading.ArchitectureTests;

public sealed class PaperScanUniverseSnapshotTests
{
    private static readonly DateTimeOffset s_boundary = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_observed = s_boundary.AddSeconds(30);

    [Fact]
    public void CapturePinsTheCompleteOrderedUniverseAndRejectsAlteredEvidence()
    {
        var owner = Guid.NewGuid();
        var members = Members();
        var captured = Capture(owner, s_boundary, s_observed, members);
        members[0] = members[0] with { MedianDailyQuoteVolume = 6_000_000m };

        captured.Validate();
        Assert.Equal(2, captured.Members.Count);
        Assert.All(captured.DailyEvidence,
            entry => Assert.Null(entry.CandleFingerprint));
        Assert.Equal(40, captured.Policy.MaximumCandidatePairs);
        Assert.Equal(5_000_000m, captured.Members[0].MedianDailyQuoteVolume);
        Assert.Throws<InvalidOperationException>(() =>
            (captured with { Members = members }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (captured with { ObservedAtUtc = s_observed.AddSeconds(1) }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (captured with
            {
                Policy = captured.Policy with { MaximumCandidatePairs = 2 }
            }).Validate());
    }

    [Fact]
    public void RefusesIncompleteOrMisleadingCandidateSets()
    {
        var owner = Guid.NewGuid();
        var members = Members();
        Assert.Throws<ArgumentException>(() => Capture(
            owner, s_boundary, s_boundary.AddSeconds(-1), members));
        Assert.Throws<ArgumentException>(() => Capture(
            owner, s_boundary.AddMinutes(1), s_observed, members));
        Assert.Throws<ArgumentException>(() => Capture(
            owner, s_boundary, s_observed, [members[0], members[0]]));
        Assert.Throws<ArgumentException>(() => Capture(
            owner, s_boundary, s_observed, [members[1], members[0]]));
        Assert.Throws<ArgumentException>(() => Capture(
            owner, s_boundary, s_observed, [members[0] with
            {
                PairFilters = new PaperExchangeFilters(0m, .001m, .001m, 10m)
            }]));
        Assert.Empty(Capture(
            owner, s_boundary, s_observed, []).Members);
        var restricted = PaperTrainingUniversePolicy.PlatformDefault with { MaximumCandidatePairs = 1 };
        Assert.Throws<ArgumentException>(() => PaperScanUniverseSnapshot.Capture(
            owner, s_boundary, s_observed, members, restricted, NoSeries()));
        Assert.Equal(1, PaperScanUniverseSnapshot.Capture(
            owner, s_boundary, s_observed, [members[0]], restricted, NoSeries())
            .Policy.MaximumCandidatePairs);
    }

    [Fact]
    public void DailyWindowFingerprintPinsOnlyClosedAsOfScannerCandles()
    {
        var owner = Guid.NewGuid();
        var lastClose = s_boundary.Date;
        var close = new DateTimeOffset(lastClose, TimeSpan.Zero);
        var candles = Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
            .Select(index =>
            {
                var open = close.AddDays(index - ApprovedConsensusStrategyProfiles.RequiredHistory);
                return new Candle("XBT/EUR", CandleInterval.OneDay, open,
                    open.AddDays(1), 10m, 11m, 9m, 10m, 100m, true, false);
            }).ToArray();
        var series = new Dictionary<(string, CandleInterval), ExperimentCandleSeries>
        {
            [("XBT/EUR", CandleInterval.OneDay)] =
                new("XBT/EUR", CandleInterval.OneDay, close, candles)
        };
        var snapshot = PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed,
            [Members()[0]], PaperTrainingUniversePolicy.PlatformDefault, series);

        snapshot.Validate();
        snapshot.VerifyDailyWindow(series[("XBT/EUR", CandleInterval.OneDay)]);
        Assert.Equal(close, snapshot.DailyEvidence[0].AsOfUtc);
        Assert.Equal(HistoricalCandleFingerprint.Compute(candles),
            snapshot.DailyEvidence[0].CandleFingerprint);
        Assert.Throws<ArgumentException>(() =>
            (snapshot with { DailyEvidence = [] }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (snapshot with { DailyEvidence =
                [snapshot.DailyEvidence[0] with { CandleFingerprint = new string('A', 64) }] })
            .Validate());
        var changed = candles.ToArray();
        changed[^1] = new Candle("XBT/EUR", CandleInterval.OneDay,
            changed[^1].OpenTimeUtc, close,
            10m, 11m, 9m, 10m, 101m, true, false);
        Assert.Throws<InvalidOperationException>(() => snapshot.VerifyDailyWindow(
            new ExperimentCandleSeries("XBT/EUR", CandleInterval.OneDay, close, changed)));
        Assert.Throws<InvalidOperationException>(() => Capture(owner, s_boundary, s_observed,
            [Members()[0]]).VerifyDailyWindow(series[("XBT/EUR", CandleInterval.OneDay)]));
    }

    [Fact]
    public void ExpandedSnapshotPinsEveryLoadedRoleAndTheExactStrategySettings()
    {
        var owner = Guid.NewGuid();
        var daily = Series("XBT/EUR", CandleInterval.OneDay);
        var hourly = Series("XBT/EUR", CandleInterval.OneHour);
        var inputs = new Dictionary<(string, CandleInterval), ExperimentCandleSeries>
        {
            [("XBT/EUR", CandleInterval.OneDay)] = daily,
            [("XBT/EUR", CandleInterval.OneHour)] = hourly
        };
        var strategies = new[]
        {
            new PaperScanStrategyEvidence("platform.ema-trend-continuation", 5,
                NormalizedDefaults("platform.ema-trend-continuation"), true),
            new PaperScanStrategyEvidence("platform.regime-switching-ensemble", 5,
                NormalizedDefaults("platform.regime-switching-ensemble"), false)
        };
        var snapshot = PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed,
            Members(), PaperTrainingUniversePolicy.PlatformDefault, inputs, strategies);

        snapshot.Validate();
        Assert.Equal(PaperScanUniverseSnapshot.ExpandedSchemaVersion, snapshot.EvidenceSchemaVersion);
        Assert.Equal(2, snapshot.SeriesEvidence?.Count);
        Assert.Equal(strategies, snapshot.StrategyEvidence);
        snapshot.VerifySeriesWindow(hourly);
        snapshot.VerifyDailyWindow(daily);
        Assert.Throws<InvalidOperationException>(() =>
            (snapshot with { SeriesEvidence =
                [snapshot.SeriesEvidence![0] with { CandleFingerprint = new string('A', 64) },
                    snapshot.SeriesEvidence[1]] }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (snapshot with { StrategyEvidence =
                [strategies[0], strategies[1] with { Evaluated = true }] }).Validate());
        Assert.Throws<ArgumentException>(() =>
            PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed, Members(),
                PaperTrainingUniversePolicy.PlatformDefault, inputs,
                [strategies[0] with { ParametersJson = "{\"apiKey\":\"do-not-store\"}" },
                    strategies[1]]));
        Assert.Throws<ArgumentException>(() =>
            PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed, Members(),
                PaperTrainingUniversePolicy.PlatformDefault, inputs,
                [strategies[0] with { ParametersJson = string.Empty, ParametersValid = false },
                    strategies[1] with { ParametersJson = "{}", ParametersValid = false }]));
        var invalidSettings = PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed,
            Members(), PaperTrainingUniversePolicy.PlatformDefault, inputs,
            [strategies[0] with { ParametersJson = string.Empty, ParametersValid = false },
                strategies[1]]);
        invalidSettings.Validate();
        Assert.False(invalidSettings.StrategyEvidence![0].ParametersValid);
        Assert.Empty(invalidSettings.StrategyEvidence[0].ParametersJson);
        Assert.Throws<ArgumentException>(() =>
            PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed, Members(),
                PaperTrainingUniversePolicy.PlatformDefault,
                new Dictionary<(string, CandleInterval), ExperimentCandleSeries>
                {
                    [("SOL/EUR", CandleInterval.OneHour)] = Series("SOL/EUR", CandleInterval.OneHour)
                }, strategies));
        var changed = hourly.Candles.ToArray();
        var last = changed[^1];
        changed[^1] = new Candle(last.Symbol, last.Interval, last.OpenTimeUtc,
            last.CloseTimeUtc, last.Open, last.High, last.Low, last.Close,
            last.Volume + 1m, true, false);
        Assert.Throws<InvalidOperationException>(() => snapshot.VerifySeriesWindow(
            new ExperimentCandleSeries("XBT/EUR", CandleInterval.OneHour, s_boundary, changed)));
        Assert.Throws<ArgumentException>(() =>
            PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed, Members(),
                PaperTrainingUniversePolicy.PlatformDefault,
                new Dictionary<(string, CandleInterval), ExperimentCandleSeries>
                {
                    [("XBT/EUR", CandleInterval.OneHour)] =
                        new("XBT/EUR", CandleInterval.OneHour,
                            s_boundary.AddHours(1), hourly.Candles)
                }, strategies));
    }

    [Fact]
    public void LegacyStoredSnapshotDeserializesWithoutInventingExpandedEvidence()
    {
        var snapshot = Capture(Guid.NewGuid(), s_boundary, s_observed, Members());
        var priorSchemaJson = JsonSerializer.Serialize(new
        {
            snapshot.OwnerId,
            snapshot.SignalBoundaryUtc,
            snapshot.ObservedAtUtc,
            snapshot.Policy,
            snapshot.Members,
            snapshot.DailyEvidence,
            snapshot.Fingerprint
        });
        var restored = JsonSerializer.Deserialize<PaperScanUniverseSnapshot>(priorSchemaJson)!;

        restored.Validate();
        Assert.Equal(PaperScanUniverseSnapshot.SchemaVersion, restored.EvidenceSchemaVersion);
        Assert.Null(restored.SeriesEvidence);
        Assert.Throws<InvalidOperationException>(() =>
            restored.VerifySeriesWindow(Series("XBT/EUR", CandleInterval.OneHour)));
        Assert.Throws<InvalidOperationException>(() =>
            (restored with { EvidenceSchemaVersion = 2 }).Validate());
    }

    [Fact]
    public async Task SuccessfulActivationSaveAppendsOneOwnerScopedSnapshotAndNeverRewritesItAsync()
    {
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var snapshot = PaperScanUniverseSnapshot.Capture(owner, s_boundary, s_observed,
            Members(), PaperTrainingUniversePolicy.PlatformDefault,
            new Dictionary<(string, CandleInterval), ExperimentCandleSeries>
            {
                [("XBT/EUR", CandleInterval.OneDay)] = Series("XBT/EUR", CandleInterval.OneDay)
            },
            [new PaperScanStrategyEvidence("platform.ema-trend-continuation", 5,
                NormalizedDefaults("platform.ema-trend-continuation"), true)]);
        await using (var context = new TradingDbContext(options))
        {
            var repository = new EfPaperTrainingActivationRepository(context);
            var activation = new PaperTrainingActivation(
                owner, PaperTrainingActivationState.Active, [],
                new(true, true, true, true, true, true), s_boundary, owner);
            Assert.True(await repository.TrySaveAsync(activation, null));
            var updated = (await repository.GetAsync(owner))! with
            {
                PendingUniverseSnapshot = snapshot,
                Qualifications = [ScanMarker(snapshot)]
            };
            Assert.False(await repository.TrySaveAsync(updated, PaperTrainingActivationState.Disabled));
            Assert.Empty(context.AuditEvents);
            Assert.True(await repository.TrySaveAsync(updated, PaperTrainingActivationState.Active));
            Assert.Single(context.AuditEvents);
            Assert.True(await repository.TrySaveAsync(updated, PaperTrainingActivationState.Active));
            Assert.Single(context.AuditEvents);
            var altered = Capture(owner, s_boundary, s_observed,
                [Members()[0] with { MedianDailyQuoteVolume = 6_000_000m }]);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.TrySaveAsync(updated with
                {
                    PendingUniverseSnapshot = altered,
                    Qualifications = [ScanMarker(altered)]
                }, PaperTrainingActivationState.Active));
            Assert.Single(context.AuditEvents);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.TrySaveAsync(updated with
                {
                    PendingUniverseSnapshot = snapshot with { OwnerId = otherOwner }
                }, PaperTrainingActivationState.Active));
        }

        await using var reopened = new TradingDbContext(options);
        var evidence = new EfPaperScanUniverseSnapshotRepository(reopened);
        var restored = await evidence.GetAsync(owner, s_boundary);
        Assert.NotNull(restored);
        Assert.Equal(snapshot.Fingerprint, restored.Fingerprint);
        Assert.Equal(snapshot.Members, restored.Members);
        Assert.Equal(snapshot.DailyEvidence, restored.DailyEvidence);
        Assert.Equal(snapshot.SeriesEvidence, restored.SeriesEvidence);
        Assert.Equal(snapshot.StrategyEvidence, restored.StrategyEvidence);
        Assert.Null(await evidence.GetAsync(otherOwner, s_boundary));
        Assert.Null(await evidence.GetAsync(owner, s_boundary.AddMinutes(-5)));
        var saved = await new EfPaperTrainingActivationRepository(reopened).GetAsync(owner);
        Assert.NotNull(saved);
        Assert.Null(saved.PendingUniverseSnapshot);
    }

    [Fact]
    public async Task AuditEventsCannotBeModifiedOrDeletedThroughTheApplicationContextAsync()
    {
        var owner = Guid.NewGuid();
        await using var context = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var activation = new PaperTrainingActivation(
            owner, PaperTrainingActivationState.Active, [],
            new(true, true, true, true, true, true), s_boundary, owner,
            Qualifications: [ScanMarker(Capture(owner, s_boundary, s_observed, Members()))],
            PendingUniverseSnapshot: Capture(owner, s_boundary, s_observed, Members()));
        Assert.True(await new EfPaperTrainingActivationRepository(context)
            .TrySaveAsync(activation, null));
        var row = await context.AuditEvents.SingleAsync();
        context.Entry(row).Property(nameof(row.CorrelationId)).CurrentValue = "forged";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        context.Entry(row).State = EntityState.Unchanged;
        context.AuditEvents.Remove(row);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    private static PaperTrainingQualificationResult ScanMarker(PaperScanUniverseSnapshot snapshot) =>
        new(0, "KRAKEN/EUR", true, 0m, 0, 0m,
            $"scan-run-{snapshot.SignalBoundaryUtc:O}",
            "Completed scanner boundary.", "platform.scanner",
            PaperOnlyExploration: true, CandleInterval.FiveMinutes,
            ScanMetrics: new(snapshot.Members.Count, 0, 0, 0, 0));

    private static PaperScanUniverseSnapshot Capture(
        Guid owner,
        DateTimeOffset boundary,
        DateTimeOffset observedAt,
        IReadOnlyList<PaperTrainingUniverseCandidate> members) =>
        PaperScanUniverseSnapshot.Capture(owner, boundary, observedAt, members,
            PaperTrainingUniversePolicy.PlatformDefault, NoSeries());

    private static Dictionary<(string Symbol, CandleInterval Interval), ExperimentCandleSeries>
        NoSeries() => [];

    private static string NormalizedDefaults(string family)
    {
        Assert.True(ApprovedStrategyParameters.TryNormalize(family, "{}", out var normalized,
            out var error), error);
        return normalized;
    }

    private static ExperimentCandleSeries Series(string symbol, CandleInterval interval)
    {
        var duration = TimeSpan.FromMinutes((int)interval);
        var close = new DateTimeOffset(
            s_boundary.UtcTicks - s_boundary.UtcTicks % duration.Ticks, TimeSpan.Zero);
        var candles = Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
            .Select(index =>
            {
                var open = close.AddTicks(
                    (index - ApprovedConsensusStrategyProfiles.RequiredHistory) * duration.Ticks);
                return new Candle(symbol, interval, open, open.Add(duration),
                    10m, 11m, 9m, 10m, 100m, true, false);
            }).ToArray();
        return new ExperimentCandleSeries(symbol, interval, close, candles);
    }

    private static PaperTrainingUniverseCandidate[] Members() =>
    [
        new("XBT/EUR", 5_000_000m, 2_000_000m, 30,
            new PaperExchangeFilters(.01m, .001m, .001m, 10m)),
        new("ETH/EUR", 4_000_000m, 1_500_000m, 30,
            new PaperExchangeFilters(.01m, .001m, .001m, 10m))
    ];
}
