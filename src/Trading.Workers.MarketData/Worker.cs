using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Trading.MarketData;

namespace Trading.Workers.MarketData;

public sealed class Worker : BackgroundService
{
    private readonly IStreamingCandleSource _source;
    private readonly IServiceScopeFactory _scopeFactory;
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
        IOptions<MarketDataStreamingOptions> options,
        ILogger<Worker> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Every source and persistence failure must be retried without ending the hosted ingestion service.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;
        var subscriptions = GetSubscriptions(options);
        if (!options.Enabled || subscriptions.Length == 0)
        {
            s_logDisabled(_logger, null);
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var candle in _source.StreamAsync(subscriptions, stoppingToken)
                    .WithCancellation(stoppingToken).ConfigureAwait(false))
                {
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<CandleIngestionProcessor>();
                    await processor.ProcessAsync(candle, stoppingToken).ConfigureAwait(false);
                }

                throw new MarketDataSourceException("The public candle stream ended unexpectedly.");
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
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled || options.Symbols.Count == 0 || options.Intervals.Count == 0)
        {
            return Array.Empty<CandleSubscription>();
        }

        return options.Symbols
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
            .SelectMany(symbol => options.Intervals.Where(interval =>
                    interval != Trading.Domain.Market.CandleInterval.None &&
                    interval != Trading.Domain.Market.CandleInterval.TenMinutes)
                .Select(interval => new CandleSubscription(symbol, interval)))
            .Distinct()
            .ToArray();
    }

    internal static TimeSpan GetReconnectDelay(int failures, int maximumSeconds)
    {
        var maximum = Math.Max(1, maximumSeconds);
        var exponential = Math.Min(maximum, Math.Pow(2, Math.Min(failures - 1, 10)));
        var jitter = 0.8d + (RandomNumberGenerator.GetInt32(0, 4_001) / 10_000d);
        return TimeSpan.FromSeconds(Math.Min(maximum, exponential * jitter));
    }
}
