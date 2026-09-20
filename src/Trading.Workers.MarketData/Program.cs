using Trading.Workers.MarketData;
using Microsoft.EntityFrameworkCore;
using Trading.Exchanges.Kraken.MarketData;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<MarketDataStreamingOptions>(
    builder.Configuration.GetSection(MarketDataStreamingOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IStreamingCandleSource, KrakenStreamingCandleSource>();
builder.Services.AddScoped<ICandleRepository, EfCandleRepository>();
builder.Services.AddScoped<CandleIngestionProcessor>();
var connectionString = builder.Configuration.GetConnectionString("TradingDb");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connectionString));
}
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
