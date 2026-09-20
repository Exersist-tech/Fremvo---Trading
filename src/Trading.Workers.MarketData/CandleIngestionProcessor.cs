using Trading.MarketData;

namespace Trading.Workers.MarketData;

public sealed class CandleIngestionProcessor
{
    private static readonly Action<ILogger, string, Trading.Domain.Market.CandleInterval, DateTimeOffset, CandleWriteResult, string, Exception?> s_logEvidence =
        LoggerMessage.Define<string, Trading.Domain.Market.CandleInterval, DateTimeOffset, CandleWriteResult, string>(
            LogLevel.Warning,
            new EventId(1, nameof(CandleIngestionProcessor)),
            "Candle ingestion evidence: Symbol={Symbol} Interval={Interval} OpenTime={OpenTime} WriteResult={WriteResult} QualityFlags={QualityFlags}");
    private readonly ICandleRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CandleIngestionProcessor> _logger;

    public CandleIngestionProcessor(
        ICandleRepository repository,
        TimeProvider timeProvider,
        ILogger<CandleIngestionProcessor> logger)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CandleWriteResult> ProcessAsync(Candle incoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var previous = await _repository.GetLatestAsync(incoming.Symbol, incoming.Interval, cancellationToken)
            .ConfigureAwait(false);
        var sameOpenTime = await _repository.ListAsync(
                incoming.Symbol, incoming.Interval, incoming.OpenTimeUtc, incoming.OpenTimeUtc, cancellationToken)
            .ConfigureAwait(false);
        var flags = incoming.QualityFlags
            .Concat(CandleQualityEvaluator.Evaluate(
                incoming, previous, _timeProvider.GetUtcNow(),
                TimeSpan.FromMinutes((int)incoming.Interval),
                TimeSpan.FromMinutes((int)incoming.Interval * 2)))
            .Concat(sameOpenTime.SelectMany(existing => CandleQualityEvaluator.Evaluate(
                incoming, existing, _timeProvider.GetUtcNow(),
                TimeSpan.FromMinutes((int)incoming.Interval),
                TimeSpan.FromMinutes((int)incoming.Interval * 2))))
            .Distinct()
            .OrderBy(flag => (int)flag)
            .ToArray();
        var normalized = new Candle(
            incoming.Symbol, incoming.Interval, incoming.OpenTimeUtc, incoming.CloseTimeUtc,
            incoming.Open, incoming.High, incoming.Low, incoming.Close, incoming.Volume,
            incoming.IsClosed, incoming.IsDerived, flags);

        var result = await _repository.UpsertAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (flags.Length != 0 || result != CandleWriteResult.Inserted)
        {
            s_logEvidence(
                _logger, normalized.Symbol, normalized.Interval, normalized.OpenTimeUtc, result,
                string.Join(',', flags), null);
        }

        return result;
    }
}
