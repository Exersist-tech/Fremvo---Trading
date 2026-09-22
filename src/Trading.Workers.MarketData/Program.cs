using Trading.Workers.MarketData;
using Microsoft.EntityFrameworkCore;
using Trading.Exchanges.Kraken.MarketData;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.MarketData;
using Trading.Infrastructure.Data.Experiments;
using Trading.MarketData;
using Trading.Application.Experiments;

var builder = Host.CreateApplicationBuilder(args);
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
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connectionString));
    builder.Services.AddScoped<EfPaperTrainingActivationRepository>();
    builder.Services.AddScoped<IPaperTrainingSubscriptionSource>(
        serviceProvider => serviceProvider.GetRequiredService<EfPaperTrainingActivationRepository>());
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
