using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class PaperTrainingMonitorTests
{
    [Fact]
    public async Task ListsOnlyCurrentOwnerActiveSlotsWithBalancesPositionsAndRecentTrades()
    {
        var ownerId = Guid.NewGuid();
        var changedAtUtc = new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with
        {
            Symbol = "XBT/EUR",
            StartingCash = 1_000m,
            Interval = Trading.Domain.Market.CandleInterval.ThirtyMinutes
        };
        var qualification = new PaperTrainingQualificationResult(
            slot.Slot,
            slot.Symbol,
            false,
            -1m,
            2,
            3m,
            new string('A', 64),
            "Unqualified paper exploration only.",
            slot.StrategyId,
            PaperOnlyExploration: true,
            Interval: slot.Interval);
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                changedAtUtc,
                ownerId,
                [qualification]),
            null);
        var workers = new InMemoryExperimentWorkerRepository();
        var worker = new ExperimentWorker(
            Guid.NewGuid(),
            ownerId,
            $"Paper training {slot.Slot} {changedAtUtc:yyyyMMddHHmmssfffffff}",
            slot.StrategyId,
            slot.Symbol,
            slot.StartingCash,
            changedAtUtc,
            slot.Seed);
        worker.Start();
        worker.ApplyPaperTrade(1m, 100m, 1m, "buy", changedAtUtc.AddHours(1));
        worker.ApplyPaperTrade(0.5m, 110m, 1m, "sell", changedAtUtc.AddHours(2));
        await workers.SaveAsync(worker);
        await workers.SaveAsync(new ExperimentWorker(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Another owner's worker",
            slot.StrategyId,
            slot.Symbol,
            1_000m,
            changedAtUtc,
            7));

        var currentPriceAt = changedAtUtc.AddHours(3);
        var candles = new StubCandleRepository(new Candle(
            slot.Symbol,
            CandleInterval.FiveMinutes,
            currentPriceAt.AddMinutes(-5),
            currentPriceAt,
            119m,
            121m,
            118m,
            120m,
            10m,
            true,
            false));
        var monitor = await new PaperTrainingMonitorService(activations, workers, candles).GetAsync(ownerId);

        var item = Assert.Single(monitor.Workers);
        Assert.Equal(PaperTrainingMonitorQualification.Exploration, item.Qualification);
        Assert.Equal(slot.Interval, item.Interval);
        Assert.Equal(PaperTrainingAutoSelectionService.ApprovedIntervals, item.AnalysisIntervals);
        Assert.Equal(ExperimentWorkerStatus.Running.ToString(), item.RuntimeStatus);
        Assert.Equal(953m, item.CashBalance);
        Assert.Equal(0.5m, item.PositionQuantity);
        Assert.Equal(101m, item.AverageEntryPrice);
        Assert.Equal(50.5m, item.PositionCost);
        Assert.Equal(120m, item.CurrentPrice);
        Assert.Equal(currentPriceAt, item.CurrentPriceAsOfUtc);
        Assert.Equal(60m, item.PositionMarketValue);
        Assert.Equal(9.5m, item.UnrealizedProfitAndLoss);
        Assert.Equal(3.5m, item.RealizedProfitAndLoss);
        Assert.Equal(0, item.AdditionCount);
        Assert.Equal(3, item.MaximumAdditions);
        Assert.Equal(2, item.TradeCount);
        Assert.Equal(["sell", "buy"], item.RecentTrades.Select(trade => trade.Direction));
    }

    [Fact]
    public async Task ShowsAnActivatedSlotWhileItsWorkerIsStillStarting()
    {
        var ownerId = Guid.NewGuid();
        var slot = PaperTrainingActivationService.ApprovedSlots[0];
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero),
                ownerId),
            null);

        var monitor = await new PaperTrainingMonitorService(
            activations,
            new InMemoryExperimentWorkerRepository(),
            new StubCandleRepository()).GetAsync(ownerId);

        var item = Assert.Single(monitor.Workers);
        Assert.Null(item.WorkerId);
        Assert.Equal("WaitingForWorker", item.RuntimeStatus);
        Assert.Null(item.CashBalance);
        Assert.Null(item.CurrentPrice);
        Assert.Null(item.PositionMarketValue);
        Assert.Null(item.UnrealizedProfitAndLoss);
        Assert.Null(item.AdditionCount);
        Assert.Null(item.MaximumAdditions);
        Assert.Empty(item.RecentTrades);
    }

    [Fact]
    public async Task UsesPreviousSafeClosedPriceWhenLatestCandleIsIncomplete()
    {
        var ownerId = Guid.NewGuid();
        var changedAtUtc = new DateTimeOffset(2026, 9, 21, 18, 0, 0, TimeSpan.Zero);
        var slot = PaperTrainingActivationService.ApprovedSlots[0] with { Symbol = "XBT/EUR" };
        var activations = new InMemoryPaperTrainingActivationRepository();
        await activations.TrySaveAsync(
            new(
                ownerId,
                PaperTrainingActivationState.Active,
                [slot],
                new(true, true, true, true, true, true),
                changedAtUtc,
                ownerId),
            null);
        var safe = new Candle(
            slot.Symbol,
            CandleInterval.FiveMinutes,
            changedAtUtc,
            changedAtUtc.AddMinutes(5),
            100m,
            101m,
            99m,
            100m,
            10m,
            true,
            false);
        var incomplete = new Candle(
            slot.Symbol,
            CandleInterval.FiveMinutes,
            changedAtUtc.AddMinutes(5),
            changedAtUtc.AddMinutes(10),
            100m,
            102m,
            100m,
            101m,
            4m,
            false,
            false);

        var monitor = await new PaperTrainingMonitorService(
            activations,
            new InMemoryExperimentWorkerRepository(),
            new StubCandleRepository(incomplete, [safe, incomplete])).GetAsync(ownerId);

        Assert.Equal(100m, Assert.Single(monitor.Workers).CurrentPrice);
    }

    private sealed class StubCandleRepository(
        Candle? latest = null,
        IReadOnlyCollection<Candle>? candles = null) : ICandleRepository
    {
        public Task<Candle?> GetLatestAsync(
            string symbol,
            CandleInterval interval,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(latest is not null
                && latest.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                && latest.Interval == interval
                    ? latest
                    : null);

        public Task<IReadOnlyCollection<Candle>> ListAsync(
            string symbol,
            CandleInterval interval,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Candle>>((candles ?? [])
                .Where(candle => candle.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)
                    && candle.Interval == interval
                    && candle.OpenTimeUtc >= fromUtc
                    && candle.OpenTimeUtc <= toUtc)
                .ToArray());

        public Task<CandleWriteResult> UpsertAsync(
            Candle candle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CandleWriteResult.Inserted);
    }
}
