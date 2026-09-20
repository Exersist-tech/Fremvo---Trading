using Trading.Application.Experiments;
using Trading.Domain.Experiments;
using Trading.MarketData.Experiments;
using Trading.Workers.Experiments;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ExperimentHostOptions>(
    builder.Configuration.GetSection(ExperimentHostOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

// This host remains inert. The approved analysis runner is intentionally not registered here:
// registering a prerequisite must not start worker analysis or paper trading.
builder.Services.AddSingleton<IExperimentWorkerRepository, InMemoryExperimentWorkerRepository>();
builder.Services.AddSingleton<ExperimentWorkerPool>();

// This is intentionally inert until an explicit training configuration selects a durable
// dataset. Registering the prerequisite must not make existing workers execute.
builder.Services.AddSingleton<IExperimentCandleSeriesSource, UnconfiguredExperimentCandleSeriesSource>();
builder.Services.AddSingleton<IExperimentResearchGroupConfigurationSource, UnconfiguredExperimentResearchGroupConfigurationSource>();
builder.Services.AddSingleton<IExperimentWorkerRunner, UnconfiguredExperimentWorkerRunner>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
