using Trading.Application.Experiments;
using Trading.Domain.Market;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData;
using Trading.MarketData.Experiments;

namespace Trading.Workers.MarketData;

public sealed class DurablePaperScanEvidenceStager : IPaperScanEvidenceStager
{
    private static readonly Action<ILogger, string, CandleInterval, DateTimeOffset, string, Exception?> s_logConflict =
        LoggerMessage.Define<string, CandleInterval, DateTimeOffset, string>(
            LogLevel.Warning, new EventId(1, "PaperAdmissionCandleConflict"),
            "Paper admission blocked by conflicting closed candle. Symbol={Symbol} Interval={Interval} OpenTime={OpenTime} ChangedFields={ChangedFields}");
    private readonly IHistoricalCandleSource _history;
    private readonly EfCandleRepository _repository;
    private readonly ILogger<DurablePaperScanEvidenceStager>? _logger;

    public DurablePaperScanEvidenceStager(
        IHistoricalCandleSource history,
        EfCandleRepository repository,
        ILogger<DurablePaperScanEvidenceStager>? logger = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger;
    }

    public Task<bool> StageAsync(
        string symbol,
        DateTimeOffset boundaryUtc,
        IReadOnlyCollection<ExperimentCandleSeries> series,
        CancellationToken cancellationToken = default) =>
        StageCoreAsync(symbol, boundaryUtc, series, requireOneMinute: true, cancellationToken);

    public Task<bool> StageUniverseDailyAsync(
        string symbol,
        DateTimeOffset boundaryUtc,
        IReadOnlyCollection<ExperimentCandleSeries> series,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(series);
        if (series.Count != 1 || series.Single().Interval != CandleInterval.OneDay)
            throw new ArgumentException("Ranked-universe staging requires one closed daily series.", nameof(series));
        return StageCoreAsync(symbol, boundaryUtc, series, requireOneMinute: false, cancellationToken);
    }

    private async Task<bool> StageCoreAsync(
        string symbol,
        DateTimeOffset boundaryUtc,
        IReadOnlyCollection<ExperimentCandleSeries> series,
        bool requireOneMinute,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(series);
        if (boundaryUtc.Offset != TimeSpan.Zero || series.Count == 0)
            throw new ArgumentException("A completed UTC scan boundary and closed series are required.", nameof(boundaryUtc));

        var windows = series.ToList();
        if (requireOneMinute && windows.All(item => item.Interval != CandleInterval.OneMinute))
        {
            IReadOnlyList<Candle> fetched;
            try
            {
                fetched = await _history.FetchAsync(
                    symbol, CandleInterval.OneMinute,
                    boundaryUtc.AddMinutes(-PaperTrainingCandleBackfillService.RequiredCandleCount - 2),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (MarketDataSourceException)
            {
                return false;
            }

            var closed = PaperTrainingCandleBackfillService.SelectContiguousClosedHistory(
                fetched, new CandleSubscription(symbol, CandleInterval.OneMinute), boundaryUtc);
            if (closed.Count != PaperTrainingCandleBackfillService.RequiredCandleCount
                || closed[^1].CloseTimeUtc != boundaryUtc)
                return false;
            windows.Add(new ExperimentCandleSeries(
                symbol, CandleInterval.OneMinute, boundaryUtc, closed));
        }

        foreach (var window in windows)
        {
            var interval = TimeSpan.FromMinutes((int)window.Interval);
            var expectedClose = new DateTimeOffset(
                boundaryUtc.UtcTicks - boundaryUtc.UtcTicks % interval.Ticks, TimeSpan.Zero);
            var candles = window.Candles;
            if (!string.Equals(window.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                || candles.Count != ApprovedConsensusStrategyProfiles.RequiredHistory
                || candles[^1].CloseTimeUtc != expectedClose
                || candles.Any(candle => !candle.CanBeUsedForClosedCandleSignal
                    || candle.IsDerived
                    || candle.Interval != window.Interval
                    || !string.Equals(candle.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                || candles.Zip(candles.Skip(1), (left, right) => left.CloseTimeUtc == right.OpenTimeUtc)
                    .Any(contiguous => !contiguous))
                return false;

            foreach (var candle in candles)
            {
                if (await _repository.UpsertAuthoritativeHistoryAsync(candle, cancellationToken)
                    .ConfigureAwait(false) == CandleWriteResult.Conflict)
                {
                    if (_logger is not null)
                    {
                        var stored = (await _repository.ListAsync(
                            symbol, window.Interval, candle.OpenTimeUtc, candle.OpenTimeUtc,
                            cancellationToken).ConfigureAwait(false)).Single();
                        s_logConflict(_logger, symbol, window.Interval, candle.OpenTimeUtc,
                            CandleQualityEvaluator.ChangedMarketDataFields(stored, candle), null);
                    }
                    return false;
                }
            }

            var persisted = await _repository.ListAsync(
                symbol, window.Interval, candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc,
                cancellationToken).ConfigureAwait(false);
            if (persisted.Count != candles.Count
                || persisted.Zip(candles, (stored, expected) =>
                        stored.CanBeUsedForClosedCandleSignal && !stored.IsDerived
                        && stored.OpenTimeUtc == expected.OpenTimeUtc
                        && stored.CloseTimeUtc == expected.CloseTimeUtc
                        && stored.Open == expected.Open && stored.High == expected.High
                        && stored.Low == expected.Low && stored.Close == expected.Close
                        && stored.Volume == expected.Volume)
                    .Any(matches => !matches))
                return false;
        }

        return true;
    }
}
