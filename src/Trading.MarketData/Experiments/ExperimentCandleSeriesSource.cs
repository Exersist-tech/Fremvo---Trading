using Trading.Domain.Market;

namespace Trading.MarketData.Experiments;

/// <summary>
/// Reads an immutable, closed-candle snapshot for experiment training. This boundary never
/// contacts an exchange and refuses partial or unsafe durable evidence rather than repairing it.
/// </summary>
public interface IExperimentCandleSeriesSource
{
    Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
        ExperimentCandleSeriesRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ExperimentCandleSeriesRequest(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset AsOfUtc,
    int MaximumCount);

public sealed record ExperimentCandleFreshnessPolicy(TimeSpan MaximumAge)
{
    public static readonly ExperimentCandleFreshnessPolicy Default = new(TimeSpan.FromHours(2));
}

public enum ExperimentCandleSeriesBlockReason
{
    None = 0,
    InvalidRequest,
    NoData,
    MaximumCountExceeded,
    FutureCandle,
    WrongSymbolOrInterval,
    DuplicateOrOutOfOrder,
    Gap,
    UnsafeCandle,
    Stale
}

public sealed class ExperimentCandleSeriesResult
{
    private ExperimentCandleSeriesResult(
        ExperimentCandleSeries? series,
        ExperimentCandleSeriesBlockReason blockReason)
    {
        Series = series;
        BlockReason = blockReason;
    }

    public ExperimentCandleSeries? Series { get; }

    public ExperimentCandleSeriesBlockReason BlockReason { get; }

    public bool IsAvailable => Series is not null;

    public static ExperimentCandleSeriesResult Available(ExperimentCandleSeries series) =>
        new(series ?? throw new ArgumentNullException(nameof(series)), ExperimentCandleSeriesBlockReason.None);

    public static ExperimentCandleSeriesResult Blocked(ExperimentCandleSeriesBlockReason reason)
    {
        if (reason == ExperimentCandleSeriesBlockReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return new(null, reason);
    }
}

/// <summary>Immutable durable-candle training input, including its fixed UTC cutoff and provenance.</summary>
public sealed class ExperimentCandleSeries
{
    public const string DurableCandleRepositoryProvenance = "durable-candle-repository";

    public ExperimentCandleSeries(
        string symbol,
        CandleInterval interval,
        DateTimeOffset asOfUtc,
        IReadOnlyList<Candle> candles,
        string datasetProvenance = DurableCandleRepositoryProvenance)
    {
        Symbol = symbol;
        Interval = interval;
        AsOfUtc = asOfUtc;
        Candles = candles;
        DatasetProvenance = datasetProvenance;
    }

    public string Symbol { get; }

    public CandleInterval Interval { get; }

    public DateTimeOffset AsOfUtc { get; }

    public IReadOnlyList<Candle> Candles { get; }

    public string DatasetProvenance { get; }
}

/// <summary>
/// Exchange-neutral experiment data boundary over the durable candle repository.
/// </summary>
public sealed class DurableExperimentCandleSeriesSource : IExperimentCandleSeriesSource
{
    public const int MaximumRequestedCount = 2_000;

    private readonly ICandleRepository _repository;
    private readonly ExperimentCandleFreshnessPolicy _freshnessPolicy;

    public DurableExperimentCandleSeriesSource(
        ICandleRepository repository,
        ExperimentCandleFreshnessPolicy? freshnessPolicy = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _freshnessPolicy = freshnessPolicy ?? ExperimentCandleFreshnessPolicy.Default;

        if (_freshnessPolicy.MaximumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(freshnessPolicy));
        }
    }

    public async Task<ExperimentCandleSeriesResult> GetClosedSeriesAsync(
        ExperimentCandleSeriesRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || !IsValidRequest(request))
        {
            return ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.InvalidRequest);
        }

        var intervalDuration = TimeSpan.FromMinutes((int)request.Interval);
        var completedBoundaryUtc = AlignDown(request.AsOfUtc, intervalDuration);
        var fromUtc = completedBoundaryUtc - TimeSpan.FromTicks(intervalDuration.Ticks * (request.MaximumCount + 1L));
        var latestCompletedOpenUtc = completedBoundaryUtc - intervalDuration;
        var persisted = (await _repository.ListAsync(
                request.Symbol,
                request.Interval,
                fromUtc,
                latestCompletedOpenUtc,
                cancellationToken)
            .ConfigureAwait(false))
            .ToArray();

        if (persisted.Length == 0)
        {
            return ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.NoData);
        }

        if (persisted.Length > request.MaximumCount + 1)
        {
            return ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.MaximumCountExceeded);
        }
        var candles = persisted.TakeLast(request.MaximumCount).ToArray();

        var validation = Validate(candles, request, intervalDuration);
        if (validation != ExperimentCandleSeriesBlockReason.None)
        {
            return ExperimentCandleSeriesResult.Blocked(validation);
        }

        var latest = candles[^1];
        var intervalFreshness = intervalDuration + intervalDuration;
        var maximumAge = intervalFreshness < _freshnessPolicy.MaximumAge
            ? intervalFreshness
            : _freshnessPolicy.MaximumAge;
        if (request.AsOfUtc - latest.CloseTimeUtc > maximumAge)
        {
            return ExperimentCandleSeriesResult.Blocked(ExperimentCandleSeriesBlockReason.Stale);
        }

        return ExperimentCandleSeriesResult.Available(new ExperimentCandleSeries(
            request.Symbol,
            request.Interval,
            request.AsOfUtc,
            Array.AsReadOnly(candles)));
    }

    private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan interval) =>
        new(value.UtcTicks - (value.UtcTicks % interval.Ticks), TimeSpan.Zero);

    private static bool IsValidRequest(ExperimentCandleSeriesRequest request) =>
        !string.IsNullOrWhiteSpace(request.Symbol) &&
        request.Interval != CandleInterval.None &&
        request.AsOfUtc != default &&
        request.AsOfUtc.Offset == TimeSpan.Zero &&
        request.MaximumCount is > 0 and <= MaximumRequestedCount;

    private static ExperimentCandleSeriesBlockReason Validate(
        IReadOnlyList<Candle> candles,
        ExperimentCandleSeriesRequest request,
        TimeSpan intervalDuration)
    {
        Candle? previous = null;
        foreach (var candle in candles)
        {
            if (!string.Equals(candle.Symbol, request.Symbol, StringComparison.Ordinal) ||
                candle.Interval != request.Interval)
            {
                return ExperimentCandleSeriesBlockReason.WrongSymbolOrInterval;
            }

            if (candle.CloseTimeUtc > request.AsOfUtc)
            {
                return ExperimentCandleSeriesBlockReason.FutureCandle;
            }

            if (!candle.CanBeUsedForClosedCandleSignal ||
                candle.CloseTimeUtc - candle.OpenTimeUtc != intervalDuration)
            {
                return ExperimentCandleSeriesBlockReason.UnsafeCandle;
            }

            if (previous is not null)
            {
                if (candle.OpenTimeUtc <= previous.OpenTimeUtc)
                {
                    return ExperimentCandleSeriesBlockReason.DuplicateOrOutOfOrder;
                }

                if (candle.OpenTimeUtc != previous.CloseTimeUtc)
                {
                    return ExperimentCandleSeriesBlockReason.Gap;
                }
            }

            previous = candle;
        }

        return ExperimentCandleSeriesBlockReason.None;
    }
}
