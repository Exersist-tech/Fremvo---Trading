using Trading.Application.Universe;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;

namespace Trading.ArchitectureTests;

public sealed class UniverseRecalculationTests
{
    private static readonly string[] SpotPermission = { "SPOT" };
    private static readonly string[] AllowedQuotes = { "USDT" };
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly EligibilityScope PaperScope =
        new(EligibilityPurpose.Paper, CandleInterval.OneHour, TradingProductType.Spot);

    private static readonly EligibilityScope LiveScope =
        new(EligibilityPurpose.SpotLive, CandleInterval.OneHour, TradingProductType.Spot);

    [Fact]
    public async Task HealthyEvidenceGrantsUpToPaper()
    {
        var (instrument, eligibility, service) = Build(Evidence.Healthy());

        var report = await service.RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { PaperScope }, CancellationToken.None);

        Assert.Equal(RecalculationOutcome.Granted, report.Changes[0].Outcome);
        Assert.True(eligibility.IsGranted(PaperScope, Now, TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task LiveIsNeverGrantedAutomatically()
    {
        var (instrument, eligibility, service) = Build(Evidence.Healthy());

        var report = await service.RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { LiveScope }, CancellationToken.None);

        Assert.False(eligibility.HasGrant(LiveScope));
        Assert.Contains("administrator", report.Changes[0].Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HighestAutomaticPurposeIsPaper() =>
        await Task.Run(() =>
            Assert.Equal(EligibilityPurpose.Paper, UniverseRecalculationService.HighestAutomaticPurpose));

    [Fact]
    public async Task MissingMetricsRevokeAnExistingGrant()
    {
        var (instrument, eligibility, service) = Build(Evidence.Healthy());

        await service.RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { PaperScope }, CancellationToken.None);
        Assert.True(eligibility.IsGranted(PaperScope, Now, TimeSpan.FromHours(6)));

        var (_, _, blind) = Build(Evidence.Blind(), instrument, eligibility);
        var report = await blind.RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { PaperScope }, CancellationToken.None);

        Assert.Equal(RecalculationOutcome.Revoked, report.Changes[0].Outcome);
        Assert.False(eligibility.IsGranted(PaperScope, Now, TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task AnEvidenceFailureRevokesRatherThanRetains()
    {
        // Not knowing must never be treated as still qualifying.
        var (instrument, eligibility, service) = Build(Evidence.Healthy());
        await service.RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { PaperScope }, CancellationToken.None);

        var (_, _, failing) = Build(Evidence.Throwing(), instrument, eligibility);
        var report = await failing.RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { PaperScope }, CancellationToken.None);

        Assert.Equal(RecalculationOutcome.Failed, report.Changes[0].Outcome);
        Assert.False(eligibility.IsGranted(PaperScope, Now, TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task OneFailingInstrumentDoesNotStopTheOthers()
    {
        var healthy = TradingInstrument();
        var broken = TradingInstrument();

        var eligibility = new Dictionary<Guid, InstrumentEligibility>
        {
            [healthy.Id] = new(healthy.Id),
            [broken.Id] = new(broken.Id)
        };

        var service = Service(Evidence.ThrowingFor(broken.Id));

        var report = await service.RecalculateAsync(
            new[] { broken, healthy }, eligibility, new[] { PaperScope }, CancellationToken.None);

        Assert.Equal(2, report.Changes.Count);
        Assert.True(eligibility[healthy.Id].IsGranted(PaperScope, Now, TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task ANewListingIsBlockedEvenWithPerfectEvidence()
    {
        var instrument = TradingInstrument(firstCandleUtc: Now.AddDays(-3));
        var eligibility = new InstrumentEligibility(instrument.Id);

        var report = await Service(Evidence.Healthy()).RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { PaperScope }, CancellationToken.None);

        Assert.False(eligibility.IsGranted(PaperScope, Now, TimeSpan.FromHours(6)));
        Assert.False(report.Changes[0].Outcome == RecalculationOutcome.Granted);
    }

    [Fact]
    public async Task ASuspendedInstrumentIsNotGranted()
    {
        var instrument = TradingInstrument();
        instrument.Suspend("Operator action.", Now);
        var eligibility = new InstrumentEligibility(instrument.Id);

        await Service(Evidence.Healthy()).RecalculateAsync(
            new[] { instrument }, Map(eligibility), new[] { PaperScope }, CancellationToken.None);

        Assert.False(eligibility.IsGranted(PaperScope, Now, TimeSpan.FromHours(6)));
    }

    [Fact]
    public async Task RecalculationIsDeterministic()
    {
        var instrumentA = TradingInstrument();
        var eligibilityA = new InstrumentEligibility(instrumentA.Id);
        var first = await Service(Evidence.Healthy()).RecalculateAsync(
            new[] { instrumentA }, Map(eligibilityA), new[] { PaperScope }, CancellationToken.None);

        var instrumentB = TradingInstrument();
        var eligibilityB = new InstrumentEligibility(instrumentB.Id);
        var second = await Service(Evidence.Healthy()).RecalculateAsync(
            new[] { instrumentB }, Map(eligibilityB), new[] { PaperScope }, CancellationToken.None);

        Assert.Equal(first.Changes[0].Outcome, second.Changes[0].Outcome);
        Assert.Equal(first.Changes[0].Directive, second.Changes[0].Directive);
        Assert.Equal(first.Changes[0].Explanation, second.Changes[0].Explanation);
        Assert.Equal(first.RecalculatedAtUtc, second.RecalculatedAtUtc);
    }

    [Fact]
    public async Task CancellationIsObserved()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var instrument = TradingInstrument();
        var eligibility = new InstrumentEligibility(instrument.Id);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(Evidence.Healthy()).RecalculateAsync(
                new[] { instrument }, Map(eligibility), new[] { PaperScope }, cts.Token));
    }

    // ---- Administrator view (task 3B.10) ----

    [Fact]
    public async Task AdminViewExplainsEveryFailingGate()
    {
        var instrument = Instrument.CreateSeed(
            Guid.NewGuid(), "Kraken", "BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);

        var views = await AdminQuery(Evidence.Blind()).GetAsync(
            new[] { instrument }, EligibilityPurpose.Research, CandleInterval.OneHour, CancellationToken.None);

        var view = Assert.Single(views);
        Assert.False(view.Eligible);
        Assert.Contains(view.Gates, gate => !gate.Passed);
        Assert.All(view.Gates, gate => Assert.False(string.IsNullOrWhiteSpace(gate.Detail)));
        Assert.False(string.IsNullOrWhiteSpace(view.Explanation));
    }

    [Fact]
    public async Task AdminViewNeverExposesCredentialsOrUserData()
    {
        var views = await AdminQuery(Evidence.Healthy()).GetAsync(
            new[] { TradingInstrument() }, EligibilityPurpose.Research, CandleInterval.OneHour,
            CancellationToken.None);

        var properties = typeof(UniverseInstrumentView).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(properties, name =>
            name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("User", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Account", StringComparison.OrdinalIgnoreCase));

        Assert.Single(views);
    }

    [Fact]
    public async Task AdminViewReportsThatReductionRemainsPermitted()
    {
        var views = await AdminQuery(Evidence.Blind()).GetAsync(
            new[] { TradingInstrument() }, EligibilityPurpose.Research, CandleInterval.OneHour,
            CancellationToken.None);

        Assert.True(views[0].PermitsReduction);
        Assert.False(views[0].PermitsNewExposure);
    }

    // ---- Helpers ----

    private static Dictionary<Guid, InstrumentEligibility> Map(InstrumentEligibility eligibility) =>
        new() { [eligibility.InstrumentId] = eligibility };

    private static (Instrument, InstrumentEligibility, UniverseRecalculationService) Build(
        IUniverseEvidenceSource evidence,
        Instrument? instrument = null,
        InstrumentEligibility? eligibility = null)
    {
        instrument ??= TradingInstrument();
        eligibility ??= new InstrumentEligibility(instrument.Id);
        return (instrument, eligibility, Service(evidence));
    }

    private static EligibilityThresholds Thresholds() => new(
        minimumRollingQuoteVolume: 1_000_000m,
        minimumMedianQuoteVolume: 500_000m,
        maximumSpread: 0.0050m,
        maximumEstimatedSlippage: 0.0060m,
        minimumHistoryCandles: 100,
        minimumListingAge: TimeSpan.FromDays(90),
        maximumEvidenceAge: TimeSpan.FromHours(6));

    private static UniverseRecalculationService Service(IUniverseEvidenceSource evidence) => new(
        new InstrumentEligibilityEvaluator(AllowedQuotes, Thresholds()),
        new InstrumentDegradationPolicy(TimeSpan.FromHours(6)),
        NewListingPolicy.PlatformFloor,
        Thresholds(),
        evidence,
        new FixedTimeProvider(Now));

    private static UniverseAdminQueryService AdminQuery(IUniverseEvidenceSource evidence) => new(
        new InstrumentEligibilityEvaluator(AllowedQuotes, Thresholds()),
        new InstrumentDegradationPolicy(TimeSpan.FromHours(6)),
        NewListingPolicy.PlatformFloor,
        Thresholds(),
        evidence,
        new FixedTimeProvider(Now));

    private static Instrument TradingInstrument(DateTimeOffset? firstCandleUtc = null)
    {
        var instrument = Instrument.CreateSeed(
            Guid.NewGuid(), "Kraken", "BTCUSDT", "BTC", "USDT", AssetClass.Cryptocurrency);

        instrument.ObserveInCatalogue(InstrumentTradingStatus.Trading, "ONLINE", SpotPermission, Now.AddMinutes(-5));
        instrument.MarkFiltersLoaded(Now.AddMinutes(-5));
        instrument.RecordFirstObservedCandle(firstCandleUtc ?? Now.AddDays(-500));
        return instrument;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Evidence : IUniverseEvidenceSource
    {
        private readonly bool _healthy;
        private readonly Guid? _throwFor;
        private readonly bool _throwAlways;

        private Evidence(bool healthy, bool throwAlways, Guid? throwFor)
        {
            _healthy = healthy;
            _throwAlways = throwAlways;
            _throwFor = throwFor;
        }

        public static Evidence Healthy() => new(healthy: true, throwAlways: false, throwFor: null);

        public static Evidence Blind() => new(healthy: false, throwAlways: false, throwFor: null);

        public static Evidence Throwing() => new(healthy: true, throwAlways: true, throwFor: null);

        public static Evidence ThrowingFor(Guid instrumentId) =>
            new(healthy: true, throwAlways: false, throwFor: instrumentId);

        public Task<InstrumentMetrics?> GetMetricsAsync(
            Guid instrumentId, CandleInterval interval, CancellationToken cancellationToken)
        {
            if (_throwAlways || instrumentId == _throwFor)
            {
                throw new InvalidOperationException("The measurement store is unavailable.");
            }

            if (!_healthy)
            {
                return Task.FromResult<InstrumentMetrics?>(null);
            }

            return Task.FromResult<InstrumentMetrics?>(new InstrumentMetrics(
                instrumentId,
                windowStartUtc: Now.AddDays(-30),
                windowEndUtc: Now.AddMinutes(-10),
                computedAtUtc: Now.AddMinutes(-5),
                rollingQuoteVolume: 50_000_000m,
                medianQuoteVolume: 40_000_000m,
                minimumQuoteVolume: 20_000_000m,
                averageSpread: 0.0004m,
                worstSpread: 0.0009m,
                estimatedSlippage: 0.0010m,
                tradeFrequency: 900m,
                dataGapCount: 0,
                staleEventCount: 0));
        }

        public Task<int> GetAvailableHistoryCandlesAsync(
            Guid instrumentId, CandleInterval interval, CancellationToken cancellationToken) =>
            Task.FromResult(_healthy ? 5_000 : 0);
    }
}
