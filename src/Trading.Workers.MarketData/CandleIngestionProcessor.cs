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
    private readonly bool _deriveTenMinuteCandles;

    public CandleIngestionProcessor(
        ICandleRepository repository,
        TimeProvider timeProvider,
        ILogger<CandleIngestionProcessor> logger,
        bool deriveTenMinuteCandles = false)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deriveTenMinuteCandles = deriveTenMinuteCandles;
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

        if (_deriveTenMinuteCandles &&
            normalized.Interval == Trading.Domain.Market.CandleInterval.OneMinute &&
            normalized.IsClosed &&
            result != CandleWriteResult.Conflict)
        {
            await PersistTenMinuteCandleAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task PersistTenMinuteCandleAsync(Candle constituent, CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(
            constituent.OpenTimeUtc.Year,
            constituent.OpenTimeUtc.Month,
            constituent.OpenTimeUtc.Day,
            constituent.OpenTimeUtc.Hour,
            constituent.OpenTimeUtc.Minute - (constituent.OpenTimeUtc.Minute % 10),
            0,
            TimeSpan.Zero);
        var end = start.AddMinutes(10);
        var constituents = await _repository.ListAsync(
                constituent.Symbol,
                Trading.Domain.Market.CandleInterval.OneMinute,
                start,
                end.AddTicks(-1),
                cancellationToken)
            .ConfigureAwait(false);

        if (constituents.Count != 10)
        {
            return;
        }

        Candle derived;
        try
        {
            derived = DerivedCandleBuilder.BuildTenMinuteCandle(
                constituents.ToArray(), constituent.Symbol, start, end, isClosed: true);
        }
        catch (ArgumentException)
        {
            return;
        }

        await _repository.UpsertAsync(derived, cancellationToken).ConfigureAwait(false);
    }
}
