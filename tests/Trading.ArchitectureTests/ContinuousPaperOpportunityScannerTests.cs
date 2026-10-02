using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Domain.Experiments;
using Trading.Domain.Identity;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class ContinuousPaperOpportunityScannerTests
{
    [Fact]
    public async Task ScanQueuesCandidatesAndReservesAtMostTenWorkersOnlyAfterConsensus()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activationRepository = new InMemoryPaperTrainingActivationRepository();
        var activationService = new PaperTrainingActivationService(
            activationRepository,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await activationService.StartScannerAsync(
            owner,
            owner,
            RoleType.User,
            new(true, true, true, true, true, true));
        var workers = new InMemoryExperimentWorkerRepository();
        var source = new RisingHistorySource(now, 8);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(
                source,
                source,
                PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activationRepository,
            workers,
            new FixedTimeProvider(now));

        var result = await scanner.ScanAsync(owner);
        var activation = await activationRepository.GetAsync(owner);

        Assert.True(result.Persisted);
        Assert.True(result.QualifiedCandidates > 10);
        Assert.Equal(10, result.AdmittedCandidates);
        Assert.Equal(10, activation!.Slots.Count);
        Assert.Equal(8, activation.Slots.Select(slot => slot.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(activation.Slots.Select(slot => slot.StrategyId).Distinct(StringComparer.Ordinal).Count() > 1);
        Assert.Empty(await workers.ListAsync(owner));
        Assert.All(activation.Slots, slot => Assert.StartsWith("scan-", slot.ProvenanceId, StringComparison.Ordinal));
        Assert.True(activation.QualificationResults.Count(result => result.StrategyId != "platform.scanner") > 10);
        var admittedObservations = activation.QualificationResults
            .Where(result => result.CandidateDisposition == PaperTrainingCandidateDisposition.Admitted)
            .ToArray();
        Assert.Equal(10, admittedObservations.Length);
        Assert.All(admittedObservations, observation =>
        {
            Assert.True(observation.Accepted);
            Assert.True(observation.BullishChecks >= observation.RequiredBullishChecks);
            Assert.NotNull(observation.SignalCloseUtc);
        });
    }

    [Fact]
    public async Task SameFiveMinuteBoundaryIsIdempotent()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var source = new RisingHistorySource(now, 2);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            new InMemoryExperimentWorkerRepository(),
            new FixedTimeProvider(now));

        var first = await scanner.ScanAsync(owner);
        var second = await scanner.ScanAsync(owner);

        Assert.True(first.EvaluatedCandidates > 0);
        Assert.Equal(0, second.EvaluatedCandidates);
        Assert.Equal(0, second.AdmittedCandidates);
    }

    [Fact]
    public async Task BearishConsensusDoesNotReserveAFlatWorker()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var source = new RisingHistorySource(now, 2, rising: false);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            new InMemoryExperimentWorkerRepository(),
            new FixedTimeProvider(now));

        await scanner.ScanAsync(owner);
        var activation = await activations.GetAsync(owner);

        Assert.NotNull(activation);
        Assert.Empty(activation.Slots);
        Assert.Contains(activation.QualificationResults, result =>
            !result.Accepted
            && result.Reason.Contains("requires an actionable BUY", StringComparison.Ordinal)
            && result.Reason.Contains("bearish consensus", StringComparison.Ordinal)
            && result.CandidateDisposition == PaperTrainingCandidateDisposition.Bearish
            && result.BullishChecks < result.RequiredBullishChecks
            && result.SignalCloseUtc is not null);
    }

    [Fact]
    public async Task ClosedScannerWorkerReleasesCapacityWithoutDeletingHistory()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var clock = new FixedTimeProvider(now);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            clock);
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var workers = new InMemoryExperimentWorkerRepository();
        var source = new RisingHistorySource(now, 1);
        var scanner = new ContinuousPaperOpportunityScanner(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            workers,
            clock);
        await scanner.ScanAsync(owner);
        var admitted = (await activations.GetAsync(owner))!.Slots;
        Assert.NotEmpty(admitted);
        foreach (var slot in admitted)
        {
            var worker = new ExperimentWorker(
                Guid.NewGuid(),
                owner,
                ContinuousPaperOpportunityScanner.WorkerName(slot),
                slot.StrategyId,
                slot.Symbol,
                slot.StartingCash,
                now,
                slot.Seed);
            worker.Start();
            worker.Complete();
            await workers.SaveAsync(worker);
        }

        clock.UtcNow = now.AddMinutes(5);
        await scanner.ScanAsync(owner);

        Assert.Empty((await activations.GetAsync(owner))!.Slots);
        Assert.Equal(admitted.Count, (await workers.ListAsync(owner)).Count);
    }

    [Fact]
    public async Task ReleasedCapacityAdmitsFreshReplacementOpportunities()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-2);
        var clock = new FixedTimeProvider(now);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            clock);
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var workers = new InMemoryExperimentWorkerRepository();
        ContinuousPaperOpportunityScanner CreateScanner(DateTimeOffset sourceNow)
        {
            var source = new RisingHistorySource(sourceNow, 2);
            return new ContinuousPaperOpportunityScanner(
                new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
                source,
                ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
                activations,
                workers,
                clock);
        }

        await CreateScanner(now).ScanAsync(owner);
        var firstAdmissions = (await activations.GetAsync(owner))!.Slots;
        Assert.NotEmpty(firstAdmissions);
        foreach (var slot in firstAdmissions)
        {
            var worker = new ExperimentWorker(
                Guid.NewGuid(),
                owner,
                ContinuousPaperOpportunityScanner.WorkerName(slot),
                slot.StrategyId,
                slot.Symbol,
                slot.StartingCash,
                now,
                slot.Seed);
            worker.Start();
            worker.Complete();
            await workers.SaveAsync(worker);
        }

        clock.UtcNow = now.AddHours(1);
        var result = await CreateScanner(clock.UtcNow).ScanAsync(owner);
        var replacementAdmissions = (await activations.GetAsync(owner))!.Slots;

        Assert.True(result.AdmittedCandidates > 0);
        Assert.NotEmpty(replacementAdmissions);
        Assert.All(replacementAdmissions, slot => Assert.DoesNotContain(firstAdmissions, previous =>
            previous.ProvenanceId.Equals(slot.ProvenanceId, StringComparison.Ordinal)));
        Assert.Equal(firstAdmissions.Count, (await workers.ListAsync(owner)).Count);
    }

    [Fact]
    public async Task ConcurrentScansNeverReserveMoreThanTenAdmissions()
    {
        var now = AlignDown(DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).AddHours(-1);
        var owner = Guid.NewGuid();
        var activations = new InMemoryPaperTrainingActivationRepository();
        var service = new PaperTrainingActivationService(
            activations,
            new InMemoryAuditEventWriter(),
            new FixedTimeProvider(now));
        await service.StartScannerAsync(owner, owner, RoleType.User, new(true, true, true, true, true, true));
        var source = new RisingHistorySource(now, 12);
        ContinuousPaperOpportunityScanner CreateScanner() => new(
            new PaperTrainingUniverseDiscovery(source, source, PaperTrainingUniversePolicy.PlatformDefault),
            source,
            ApprovedExperimentStrategyRegistry.CreatePlatformDefault(),
            activations,
            new InMemoryExperimentWorkerRepository(),
            new FixedTimeProvider(now));

        await Task.WhenAll(CreateScanner().ScanAsync(owner), CreateScanner().ScanAsync(owner));

        var activation = await activations.GetAsync(owner);
        Assert.NotNull(activation);
        Assert.InRange(activation.Slots.Count, 1, ExperimentWorker.MaxWorkersPerUser);
        Assert.Equal(activation.Slots.Count, activation.Slots.Select(slot => slot.Slot).Distinct().Count());
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - value.UtcTicks % interval.Ticks, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class RisingHistorySource(DateTimeOffset now, int pairCount, bool rising = true) :
        ITradablePairSource,
        IHistoricalCandleSource
    {
        private readonly string[] _symbols = Enumerable.Range(1, pairCount)
            .Select(index => $"COIN{index:D2}/EUR")
            .ToArray();

        public Task<IReadOnlyList<TradablePair>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TradablePair>>(_symbols.Select(symbol =>
            {
                var parts = symbol.Split('/');
                return new TradablePair(
                    symbol.Replace("/", string.Empty, StringComparison.Ordinal),
                    symbol,
                    parts[0],
                    parts[1],
                    true,
                    0.0001m,
                    0.0001m,
                    0.0001m);
            }).ToArray());

        public Task<IReadOnlyList<Candle>> FetchAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interval == CandleInterval.OneDay)
            {
                var close = AlignDown(now, TimeSpan.FromDays(1));
                return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                    .Select(index => Candle(
                        symbol,
                        interval,
                        close.AddDays(index - ApprovedConsensusStrategyProfiles.RequiredHistory),
                        Price(index),
                        20_000m))
                    .ToArray());
            }

            var duration = TimeSpan.FromMinutes((int)interval);
            var closeUtc = AlignDown(now, duration);
            return Task.FromResult<IReadOnlyList<Candle>>(Enumerable.Range(0, ApprovedConsensusStrategyProfiles.RequiredHistory)
                .Select(index =>
                {
                    var open = closeUtc.AddTicks(duration.Ticks * (index - ApprovedConsensusStrategyProfiles.RequiredHistory));
                    var price = Price(index);
                    return Candle(
                        symbol,
                        interval,
                        open,
                        price,
                        index == ApprovedConsensusStrategyProfiles.RequiredHistory - 1 ? 200m : 100m);
                })
                .ToArray());
        }

        private decimal Price(int index) => rising ? 100m + index : 500m - index;

        private static Candle Candle(
            string symbol,
            CandleInterval interval,
            DateTimeOffset open,
            decimal price,
            decimal volume)
        {
            var duration = TimeSpan.FromMinutes((int)interval);
            return new Candle(
                symbol,
                interval,
                open,
                open.Add(duration),
                price,
                price + 1m,
                price - 1m,
                price + 0.5m,
                volume,
                true,
                false);
        }
    }
}
