using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Experiments;
using Trading.Risk;
using Trading.Workers.Experiments;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ExperimentHostOptions>(
    builder.Configuration.GetSection(ExperimentHostOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);

// Experiment state is paper-only and non-durable in this host. Nothing here can reach a live
// exchange: the pipeline runs in Paper mode and only the paper execution adapter is registered.
builder.Services.AddSingleton<IExperimentWorkerRepository, InMemoryExperimentWorkerRepository>();
builder.Services.AddSingleton<IMarketEventRepository, InMemoryMarketEventRepository>();
builder.Services.AddSingleton<IStrategyDecisionRepository, InMemoryStrategyDecisionRepository>();
builder.Services.AddSingleton<ITradeIntentRepository, InMemoryTradeIntentRepository>();
builder.Services.AddSingleton<IRiskEvaluationRepository, InMemoryRiskEvaluationRepository>();
builder.Services.AddSingleton<IExecutionCommandRepository, InMemoryExecutionCommandRepository>();
builder.Services.AddSingleton<IPortfolioUpdateRepository, InMemoryPortfolioUpdateRepository>();
builder.Services.AddSingleton<IAuditEventWriter, InMemoryAuditEventWriter>();

builder.Services.AddSingleton<RiskEngine>();
builder.Services.AddSingleton<IExecutionAdapter, PaperExecutionAdapter>();
builder.Services.AddSingleton<TradePipeline>();
builder.Services.AddSingleton<ExperimentWorkerPool>();

builder.Services.AddSingleton<IExperimentMarketFeed, UnconfiguredExperimentMarketFeed>();
builder.Services.AddSingleton<IApprovedStrategyTemplateFactory, UnconfiguredStrategyTemplateFactory>();
builder.Services.AddSingleton<IExperimentWorkerRunner, PaperExperimentWorkerRunner>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
