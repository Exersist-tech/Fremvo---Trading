using Microsoft.Extensions.Logging.Abstractions;
using Trading.Application.Scanner;
using Trading.Domain.Market;
using Trading.Domain.Scanner;
using Trading.MarketData;
using Trading.Workers.Scanner;

namespace Trading.ArchitectureTests;

public sealed class ScannerSchedulingWorkerTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DisabledAndInvalidOptionsRefuseToRun()
    {
        var source = new RecordingWorkSource(ConfiguredWork());
        using var disabled = CreateWorker(source, new RecordingResults(), new ScannerSchedulingOptions());

        var disabledReport = await disabled.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ScannerPassStatus.Disabled, disabledReport.Status);
        Assert.Equal(0, source.ListCalls);

        using var invalid = CreateWorker(source, new RecordingResults(), new ScannerSchedulingOptions
        {
            Enabled = true,
            Cadence = ScannerSchedulingOptions.MinimumCadence - TimeSpan.FromTicks(1)
        });
        var invalidReport = await invalid.RunOnceAsync(CancellationToken.None);
        Assert.Equal(ScannerPassStatus.Failed, invalidReport.Status);
        Assert.Equal(0, source.ListCalls);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScannerSchedulingOptions
        {
            Cadence = ScannerSchedulingOptions.MaximumCadence + TimeSpan.FromTicks(1)
        }.Validate());
    }

    [Fact]
    public async Task ConfiguredRequestRunsWithOwnerScopedReadAndPersistence()
    {
        var work = ConfiguredWork();
        var source = new RecordingWorkSource(work);
        var results = new RecordingResults();

        using var worker = CreateWorker(source, results);
        var report = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ScannerPassStatus.Completed, report.Status);
        Assert.Equal(1, report.Completed);
        Assert.Equal(1, report.Inserted);
        Assert.Equal(work.Item.OwnerId, source.LoadOwnerId);
        var persisted = Assert.Single(results.Results);
        Assert.Equal(work.Item.OwnerId, results.OwnerIds.Single());
        Assert.Equal(work.Item.OwnerId, persisted.OwnerId);
        Assert.Equal(work.Item.ScanRequestId, persisted.ScanRequestId);
        Assert.Equal(work.Item.ScanRunId, persisted.ScanRunId);
    }

    [Fact]
    public async Task CancellationIsPropagatedWithoutAnyFabricatedWork()
    {
        var source = new RecordingWorkSource(ConfiguredWork());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        using var worker = CreateWorker(source, new RecordingResults());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(cancelled.Token));
        Assert.Equal(0, source.ListCalls);
    }

    [Fact]
    public async Task FailedRequestDoesNotStopIndependentRequest()
    {
        var failing = ConfiguredWork();
        var succeeding = ConfiguredWork();
        var source = new RecordingWorkSource(failing, succeeding) { FailingRequestId = failing.Item.ScanRequestId };
        var results = new RecordingResults();

        using var worker = CreateWorker(source, results);
        var report = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, report.Scheduled);
        Assert.Equal(1, report.Completed);
        Assert.Equal(1, report.Failed);
        Assert.Single(results.Results);
        Assert.Equal(succeeding.Item.ScanRequestId, results.Results.Single().ScanRequestId);
    }

    [Fact]
    public async Task UnconfiguredSourceRunsNoFabricatedScan()
    {
        var results = new RecordingResults();
        using var worker = CreateWorker(new UnconfiguredScanWorkSource(), results);
        var report = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(ScannerPassStatus.UnconfiguredSource, report.Status);
        Assert.Empty(results.Results);
    }

    [Fact]
    public async Task SameRunUsesRepositoryIdempotencyWithoutConflictingRecords()
    {
        var work = ConfiguredWork();
        var results = new RecordingResults();
        using var worker = CreateWorker(new RecordingWorkSource(work), results);

        var first = await worker.RunOnceAsync(CancellationToken.None);
        var second = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, first.Inserted);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(0, second.Conflicts);
        Assert.Single(results.Results);
    }

    [Fact]
    public async Task UnsafeEvaluatorEvidencePersistsNoFakeCandidate()
    {
        var work = ConfiguredWork(isClosed: false);
        var results = new RecordingResults();

        using var worker = CreateWorker(new RecordingWorkSource(work), results);
        var report = await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, report.NoResults);
        Assert.Empty(results.Results);
    }

    private static ScannerSchedulingWorker CreateWorker(
        IScanWorkSource source,
        IScanResultRepository results,
        ScannerSchedulingOptions? options = null) =>
        new(
            NullLogger<ScannerSchedulingWorker>.Instance,
            source,
            results,
            options ?? new ScannerSchedulingOptions { Enabled = true },
            new FixedTimeProvider(s_now));

    private static WorkFixture ConfiguredWork(bool isClosed = true)
    {
        var ownerId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var request = new ScanRequest(
            requestId, ownerId, "Safe test scan", ["BTC/USD"], CandleInterval.OneHour,
            [new ScanCriterion(ScanCriterionKind.MinimumCandleVolume, 1m)], 1, s_now.AddDays(-1));
        var item = new ScheduledScanWork(ownerId, requestId, Guid.NewGuid(), s_now);
        var candle = new Candle(
            "BTC/USD", CandleInterval.OneHour, s_now.AddHours(-1), s_now,
            100m, 101m, 99m, 100m, 2m, isClosed, false);
        return new WorkFixture(item, new ScanWorkData(
            request,
            new Dictionary<string, IReadOnlyList<Candle>> { ["BTC/USD"] = [candle] }));
    }

    private sealed record WorkFixture(ScheduledScanWork Item, ScanWorkData Data);

    private sealed class RecordingWorkSource(params WorkFixture[] fixtures) : IScanWorkSource
    {
        private readonly Dictionary<Guid, WorkFixture> _fixtures = fixtures.ToDictionary(fixture => fixture.Item.ScanRequestId);

        internal int ListCalls { get; private set; }
        internal Guid? LoadOwnerId { get; private set; }
        internal Guid? FailingRequestId { get; init; }
        public bool IsConfigured => true;

        public Task<IReadOnlyList<ScheduledScanWork>> ListScheduledAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            return Task.FromResult<IReadOnlyList<ScheduledScanWork>>(_fixtures.Values.Select(fixture => fixture.Item).ToArray());
        }

        public Task<ScanWorkData?> LoadAsync(Guid ownerId, Guid scanRequestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadOwnerId = ownerId;
            if (scanRequestId == FailingRequestId)
            {
                throw new InvalidOperationException("Simulated source failure.");
            }

            return Task.FromResult<ScanWorkData?>(_fixtures[scanRequestId].Data);
        }
    }

    private sealed class RecordingResults : IScanResultRepository
    {
        private readonly Dictionary<(Guid RequestId, Guid RunId, string Symbol), ScanResult> _byIdentity = [];

        internal List<ScanResult> Results => _byIdentity.Values.ToList();
        internal List<Guid> OwnerIds { get; } = [];

        public Task<ScanResultWriteResult> RecordAsync(Guid ownerId, ScanResult result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OwnerIds.Add(ownerId);
            var key = (result.ScanRequestId, result.ScanRunId, result.Symbol);
            if (!_byIdentity.TryGetValue(key, out var existing))
            {
                _byIdentity.Add(key, result);
                return Task.FromResult(ScanResultWriteResult.Inserted);
            }

            return Task.FromResult(Equivalent(existing, result)
                ? ScanResultWriteResult.Duplicate
                : ScanResultWriteResult.Conflict);
        }

        public Task<IReadOnlyList<ScanResult>> ListAsync(
            Guid ownerId, Guid scanRequestId, Guid scanRunId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScanResult>>(Results);

        private static bool Equivalent(ScanResult left, ScanResult right) =>
            left.OwnerId == right.OwnerId &&
            left.Rank == right.Rank &&
            left.Score == right.Score &&
            left.EvidenceAsOfUtc == right.EvidenceAsOfUtc &&
            left.EvaluatedAtUtc == right.EvaluatedAtUtc &&
            left.MatchedCriteria.SequenceEqual(right.MatchedCriteria);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
