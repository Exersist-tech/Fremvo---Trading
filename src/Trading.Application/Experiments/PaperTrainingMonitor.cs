using Trading.Domain.Experiments;
using Trading.Domain.Market;
using Trading.MarketData;

namespace Trading.Application.Experiments;

public enum PaperTrainingMonitorQualification
{
    Unknown = 0,
    Qualified,
    Exploration
}

public sealed record PaperTrainingTradeMonitorItem(
    string Direction,
    decimal Quantity,
    decimal ExecutionPrice,
    decimal Fee,
    DateTimeOffset OccurredAtUtc);

public sealed record PaperTrainingWorkerMonitorItem(
    int Slot,
    string StrategyId,
    string Symbol,
    CandleInterval Interval,
    IReadOnlyList<CandleInterval> AnalysisIntervals,
    PaperTrainingMonitorQualification Qualification,
    Guid? WorkerId,
    string RuntimeStatus,
    int Seed,
    decimal StartingCash,
    decimal? CashBalance,
    decimal? PositionQuantity,
    decimal? AverageEntryPrice,
    decimal? PositionCost,
    decimal? CurrentPrice,
    DateTimeOffset? CurrentPriceAsOfUtc,
    decimal? PositionMarketValue,
    decimal? UnrealizedProfitAndLoss,
    decimal? RealizedProfitAndLoss,
    int? AdditionCount,
    int? MaximumAdditions,
    int TradeCount,
    string? FailureReason,
    IReadOnlyList<PaperTrainingTradeMonitorItem> RecentTrades);

public sealed record PaperTrainingMonitor(
    PaperTrainingActivationState State,
    IReadOnlyList<PaperTrainingWorkerMonitorItem> Workers);

/// <summary>
/// Owner-scoped, read-only projection of the currently activated paper workers and their
/// immutable simulated trade ledgers.
/// </summary>
public sealed class PaperTrainingMonitorService
{
    private const int RecentTradeLimit = 10;
    private readonly IPaperTrainingActivationReader _activations;
    private readonly IExperimentWorkerRepository _workers;
    private readonly ICandleRepository _candles;

    public PaperTrainingMonitorService(
        IPaperTrainingActivationReader activations,
        IExperimentWorkerRepository workers,
        ICandleRepository candles)
    {
        _activations = activations ?? throw new ArgumentNullException(nameof(activations));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _candles = candles ?? throw new ArgumentNullException(nameof(candles));
    }

    public async Task<PaperTrainingMonitor> GetAsync(
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
            throw new ArgumentException("Owner is required.", nameof(ownerId));

        var activation = await _activations.GetAsync(ownerId, cancellationToken).ConfigureAwait(false);
        if (activation is not { IsActive: true })
            return new(activation?.State ?? PaperTrainingActivationState.Inactive, []);

        var sessionSuffix = activation.ChangedAtUtc.ToString(
            "yyyyMMddHHmmssfffffff",
            System.Globalization.CultureInfo.InvariantCulture);
        var workerNames = activation.Slots
            .Select(slot => $"Paper training {slot.Slot} {sessionSuffix}")
            .ToArray();
        var workers = await _workers
            .ListByNamesAsync(ownerId, workerNames, cancellationToken)
            .ConfigureAwait(false);
        var latestPrices = new Dictionary<string, Candle?>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in activation.Slots.Select(slot => slot.Symbol).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            latestPrices[symbol] = await GetLatestClosedPriceAsync(symbol, cancellationToken).ConfigureAwait(false);
        }
        var qualifications = activation.QualificationResults.ToDictionary(result => result.Slot);
        var items = activation.Slots
            .OrderBy(slot => slot.Slot)
            .Select(slot =>
            {
                var expectedName = $"Paper training {slot.Slot} {sessionSuffix}";
                var worker = workers.SingleOrDefault(candidate =>
                    candidate.Name.Equals(expectedName, StringComparison.Ordinal)
                    && candidate.StrategyId.Equals(slot.StrategyId, StringComparison.Ordinal)
                    && candidate.MarketSymbol.Equals(slot.Symbol, StringComparison.Ordinal));
                qualifications.TryGetValue(slot.Slot, out var qualification);
                var qualificationStatus = qualification?.Accepted == true
                    ? PaperTrainingMonitorQualification.Qualified
                    : qualification?.PaperOnlyExploration == true
                        ? PaperTrainingMonitorQualification.Exploration
                        : PaperTrainingMonitorQualification.Unknown;
                latestPrices.TryGetValue(slot.Symbol, out var latestCandle);
                var currentPrice = latestCandle?.Close;
                decimal? positionMarketValue = worker is not null && currentPrice is decimal marketPrice
                    ? checked(worker.PositionQuantity * marketPrice)
                    : null;
                decimal? unrealizedProfitAndLoss = worker is not null && currentPrice is decimal latestPrice
                    ? checked(worker.PositionQuantity * (latestPrice - worker.AverageEntryPrice))
                    : null;

                return new PaperTrainingWorkerMonitorItem(
                    slot.Slot,
                    slot.StrategyId,
                    slot.Symbol,
                    slot.Interval,
                    PaperTrainingAutoSelectionService.ApprovedIntervals,
                    qualificationStatus,
                    worker?.Id,
                    worker?.Status.ToString() ?? "WaitingForWorker",
                    slot.Seed,
                    slot.StartingCash,
                    worker?.CashBalance,
                    worker?.PositionQuantity,
                    worker?.AverageEntryPrice,
                    worker is null ? null : worker.PositionQuantity * worker.AverageEntryPrice,
                    currentPrice,
                    latestCandle?.CloseTimeUtc,
                    positionMarketValue,
                    unrealizedProfitAndLoss,
                    worker?.RealizedProfitAndLoss,
                    worker?.AdditionCount,
                    worker?.PositionControls.MaxAdditionsPerPosition,
                    worker?.Ledger.Count ?? 0,
                    worker?.FailureReason,
                    worker?.Ledger
                        .OrderByDescending(trade => trade.OccurredAtUtc)
                        .ThenByDescending(trade => trade.Id)
                        .Take(RecentTradeLimit)
                        .Select(trade => new PaperTrainingTradeMonitorItem(
                            trade.Direction,
                            trade.Quantity,
                            trade.ExecutionPrice,
                            trade.Fee,
                            trade.OccurredAtUtc))
                        .ToArray()
                        ?? []);
            })
            .ToArray();

        return new(activation.State, items);
    }

    private async Task<Candle?> GetLatestClosedPriceAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var latest = await _candles
            .GetLatestAsync(symbol, CandleInterval.FiveMinutes, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null || latest.CanBeUsedForClosedCandleSignal)
            return latest;

        var candidates = await _candles.ListAsync(
            symbol,
            CandleInterval.FiveMinutes,
            latest.OpenTimeUtc.AddDays(-1),
            latest.OpenTimeUtc,
            cancellationToken).ConfigureAwait(false);
        return candidates
            .Where(candle => candle.CanBeUsedForClosedCandleSignal)
            .OrderByDescending(candle => candle.CloseTimeUtc)
            .ThenByDescending(candle => candle.OpenTimeUtc)
            .FirstOrDefault();
    }
}
