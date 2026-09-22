using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Trading.Application.Experiments;
using Trading.MarketData;

namespace Trading.Workers.MarketData;

public sealed class Worker : BackgroundService
{
    private readonly IStreamingCandleSource _source;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PaperTrainingCandleBackfillService _backfill;
    private readonly IOptions<MarketDataStreamingOptions> _options;
    private readonly ILogger<Worker> _logger;
    private static readonly Action<ILogger, Exception?> s_logDisabled =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(Worker)),
            "Public candle streaming is disabled or has no configured subscriptions.");
    private static readonly Action<ILogger, TimeSpan, int, Exception?> s_logReconnect =
        LoggerMessage.Define<TimeSpan, int>(LogLevel.Warning, new EventId(2, nameof(Worker)),
            "Public candle stream disconnected; retrying in {Delay}. Attempt={Attempt}");

    public Worker(
        IStreamingCandleSource source,
        IServiceScopeFactory scopeFactory,
        PaperTrainingCandleBackfillService backfill,
        IOptions<MarketDataStreamingOptions> options,
        ILogger<Worker> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _backfill = backfill ?? throw new ArgumentNullException(nameof(backfill));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every source and persistence failure must be retried without ending the hosted ingestion service.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            s_logDisabled(_logger, null);
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var subscriptions = await GetSubscriptionsAsync(options, stoppingToken).ConfigureAwait(false);
                if (subscriptions.Length == 0)
                {
                    s_logDisabled(_logger, null);
                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Max(1, options.SubscriptionRefreshSeconds)),
                        stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await _backfill.EnsureAsync(subscriptions, stoppingToken).ConfigureAwait(false);

#pragma warning disable CA2025 // The monitor is awaited in the finally block before this source is disposed.
                var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
#pragma warning restore CA2025
                Task<Exception?>? monitor = null;
                try
                {
#pragma warning disable CA2025 // The monitor is awaited in the finally block before connection is disposed.
                    monitor = MonitorSubscriptionsAsync(options, subscriptions, connection, stoppingToken);
#pragma warning restore CA2025
                    await foreach (var candle in _source.StreamAsync(subscriptions, connection.Token)
                        .WithCancellation(connection.Token).ConfigureAwait(false))
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var processor = scope.ServiceProvider.GetRequiredService<CandleIngestionProcessor>();
                        await processor.ProcessAsync(candle, connection.Token).ConfigureAwait(false);
                    }

                    throw new MarketDataSourceException("The public candle stream ended unexpectedly.");
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && connection.IsCancellationRequested)
                {
                    var monitorFailure = await monitor!.ConfigureAwait(false);
                    if (monitorFailure is not null)
                        throw new MarketDataSourceException("Paper-training subscriptions could not be refreshed.", monitorFailure);
                    failures = 0;
                    continue;
                }
                finally
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    if (monitor is not null)
                        _ = await monitor.ConfigureAwait(false);
                    connection.Dispose();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failures++;
                var delay = GetReconnectDelay(failures, options.MaximumReconnectDelaySeconds);
                s_logReconnect(_logger, delay, failures, exception);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal static CandleSubscription[] GetSubscriptions(MarketDataStreamingOptions options)
        => GetSubscriptions(options, Array.Empty<PaperTrainingMarketSubscription>());

    internal static CandleSubscription[] GetSubscriptions(
        MarketDataStreamingOptions options,
        IReadOnlyCollection<PaperTrainingMarketSubscription> activePaperSubscriptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(activePaperSubscriptions);
        if (!options.Enabled)
        {
            return Array.Empty<CandleSubscription>();
        }

        var configured = options.Symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .SelectMany(symbol => options.Intervals.Where(interval =>
                    interval != Trading.Domain.Market.CandleInterval.None &&
                    interval != Trading.Domain.Market.CandleInterval.TenMinutes)
                .Select(interval => new CandleSubscription(symbol, interval)));
        var activated = activePaperSubscriptions
            .Where(subscription =>
                !string.IsNullOrWhiteSpace(subscription.Symbol)
                && PaperTrainingAutoSelectionService.ApprovedIntervals.Contains(subscription.Interval))
            .Select(subscription => new CandleSubscription(subscription.Symbol, subscription.Interval));

        return configured
            .Concat(activated)
            .Distinct()
            .ToArray();
    }

    private async Task<CandleSubscription[]> GetSubscriptionsAsync(
        MarketDataStreamingOptions options,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<IPaperTrainingSubscriptionSource>();
        var activeSubscriptions = await source
            .GetActiveSubscriptionsAsync(cancellationToken)
            .ConfigureAwait(false);
        return GetSubscriptions(options, activeSubscriptions);
    }

    private async Task<Exception?> MonitorSubscriptionsAsync(
        MarketDataStreamingOptions options,
        IReadOnlyCollection<CandleSubscription> current,
        CancellationTokenSource connection,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(Math.Max(1, options.SubscriptionRefreshSeconds)),
                    connection.Token).ConfigureAwait(false);
                var refreshed = await GetSubscriptionsAsync(options, connection.Token).ConfigureAwait(false);
                if (!HaveSameSubscriptions(current, refreshed))
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    return null;
                }
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested || stoppingToken.IsCancellationRequested)
        {
            return null;
        }
#pragma warning disable CA1031 // The stream must reconnect when subscription discovery fails.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            await connection.CancelAsync().ConfigureAwait(false);
            return exception;
        }

        return null;
    }

    internal static bool HaveSameSubscriptions(
        IReadOnlyCollection<CandleSubscription> left,
        IReadOnlyCollection<CandleSubscription> right) =>
        left.Count == right.Count && left.ToHashSet().SetEquals(right);

    internal static TimeSpan GetReconnectDelay(int failures, int maximumSeconds)
    {
        var maximum = Math.Max(1, maximumSeconds);
        var exponential = Math.Min(maximum, Math.Pow(2, Math.Min(failures - 1, 10)));
        var jitter = 0.8d + (RandomNumberGenerator.GetInt32(0, 4_001) / 10_000d);
        return TimeSpan.FromSeconds(Math.Min(maximum, exponential * jitter));
    }
}
