using Trading.Workers.MarketData;
using Microsoft.EntityFrameworkCore;
using Trading.Exchanges.Kraken.MarketData;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.MarketData;
using Trading.Infrastructure.Data.Experiments;
using Trading.Domain.Experiments;
using Trading.MarketData;
using Trading.Application.Experiments;
using Trading.Application.Entitlements;
using Trading.Infrastructure.Data.Entitlements;

var builder = Host.CreateApplicationBuilder(args);
var telemetryConnection = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(telemetryConnection))
{
    builder.Services.AddApplicationInsightsTelemetryWorkerService(options =>
    {
        options.ConnectionString = telemetryConnection;
        options.EnableAdaptiveSampling = false;
    });
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>(
        typeof(PaperHostHeartbeatWorker).FullName!, LogLevel.Information);
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>(
        typeof(Worker).FullName!, LogLevel.Information);
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>(
        typeof(ContinuousPaperScannerWorker).FullName!, LogLevel.Information);
}
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.Services.Configure<MarketDataStreamingOptions>(
    builder.Configuration.GetSection(MarketDataStreamingOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IStreamingCandleSource, KrakenStreamingCandleSource>();
builder.Services.AddScoped<EfCandleRepository>();
builder.Services.AddScoped<ICandleRepository>(
    serviceProvider => serviceProvider.GetRequiredService<EfCandleRepository>());
builder.Services.AddSingleton<ITradablePairSource>(
    serviceProvider => new KrakenTradablePairSource(
        CreateKrakenClient(),
        serviceProvider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IHistoricalCandleSource>(serviceProvider =>
    new KrakenHistoricalCandleSource(
        CreateKrakenClient(),
        serviceProvider.GetRequiredService<ITradablePairSource>()));
builder.Services.AddSingleton<PaperTrainingCandleBackfillService>();
builder.Services.AddScoped<CandleIngestionProcessor>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<MarketDataStreamingOptions>>().Value;
    return new CandleIngestionProcessor(
        serviceProvider.GetRequiredService<ICandleRepository>(),
        serviceProvider.GetRequiredService<TimeProvider>(),
        serviceProvider.GetRequiredService<ILogger<CandleIngestionProcessor>>(),
        options.DeriveTenMinuteCandles &&
        options.Enabled &&
        options.Intervals.Contains(Trading.Domain.Market.CandleInterval.OneMinute));
});
var connectionString = builder.Configuration.GetConnectionString("TradingDb");
var streamingEnabled = builder.Configuration.GetValue<bool>("MarketDataStreaming:Enabled");
if (streamingEnabled)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "MarketDataStreaming:Enabled requires ConnectionStrings:TradingDb; no scanner or feed was started.");
    }

    builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connectionString));
    builder.Services.AddScoped<IEntitlementRepository, EfEntitlementRepository>();
    builder.Services.AddScoped<IPaperWorkerAdmissionLimit>(provider =>
        new EntitlementPaperWorkerAdmissionLimit(
            provider.GetRequiredService<IEntitlementRepository>(),
            builder.Configuration.GetValue<bool>("Entitlements:PaperWorkerLimitsEnabled")));
    builder.Services.AddHostedService<PaperHostSchemaReadiness>();
    builder.Services.AddScoped<EfPaperTrainingActivationRepository>();
    builder.Services.AddScoped<IPaperTrainingActivationRepository>(
        serviceProvider => serviceProvider.GetRequiredService<EfPaperTrainingActivationRepository>());
    builder.Services.AddScoped<IPaperTrainingActivationSource>(
        serviceProvider => serviceProvider.GetRequiredService<EfPaperTrainingActivationRepository>());
    builder.Services.AddScoped<IPaperTrainingSubscriptionSource>(
        serviceProvider => serviceProvider.GetRequiredService<EfPaperTrainingActivationRepository>());
    builder.Services.AddScoped<EfExperimentWorkerRepository>();
    builder.Services.AddScoped<EfExperimentPaperExecutionLedger>();
    builder.Services.AddScoped<IExperimentPaperExecutionLedger>(
        serviceProvider => serviceProvider.GetRequiredService<EfExperimentPaperExecutionLedger>());
    builder.Services.AddScoped<IExperimentWorkerRepository>(
        serviceProvider => serviceProvider.GetRequiredService<EfExperimentWorkerRepository>());
    builder.Services.AddSingleton(PaperTrainingUniversePolicy.PlatformDefault);
    builder.Services.AddScoped<PaperTrainingUniverseDiscovery>();
    builder.Services.AddSingleton<ApprovedExperimentStrategyRegistry>(
        _ => ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
    builder.Services.AddScoped<ContinuousPaperOpportunityScanner>();
    builder.Services.AddScoped<IPaperScanEvidenceStager, DurablePaperScanEvidenceStager>();
    builder.Services.AddHostedService<ContinuousPaperScannerWorker>();
    builder.Services.AddHostedService<PaperHostHeartbeatWorker>();
}
else
{
    builder.Services.AddScoped<IPaperTrainingSubscriptionSource, DisabledPaperTrainingActivationSource>();
}
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

static HttpClient CreateKrakenClient() => new()
{
    BaseAddress = new Uri("https://api.kraken.com"),
    Timeout = TimeSpan.FromSeconds(20)
};
