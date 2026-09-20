using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Experiments;
using Trading.Domain.Execution;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData.Experiments;
using Trading.MarketData;
using Microsoft.EntityFrameworkCore;
using Trading.Workers.Experiments;
using Trading.Risk;

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
// Activation is fail-closed by default. A deployment must explicitly replace this source with a
// durable, audited activation repository; host configuration alone can never start training.
builder.Services.AddSingleton<IPaperTrainingActivationSource, DisabledPaperTrainingActivationSource>();
var paperTrainingEnabled = builder.Configuration.GetValue<bool>("Experiments:PaperTraining:Enabled");
if (paperTrainingEnabled)
{
    var connectionString = builder.Configuration.GetConnectionString("TradingDb");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Experiments:PaperTraining:Enabled requires ConnectionStrings:TradingDb; no paper workers were started.");
    }

    builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connectionString));
    builder.Services.AddScoped<EfPaperTrainingActivationRepository>();
    builder.Services.AddScoped<IPaperTrainingActivationRepository>(provider =>
        provider.GetRequiredService<EfPaperTrainingActivationRepository>());
    builder.Services.AddScoped<IPaperTrainingActivationSource>(provider =>
        provider.GetRequiredService<EfPaperTrainingActivationRepository>());
    builder.Services.AddSingleton<IPaperTrainingActivationSource, ScopedPaperTrainingActivationSource>();
    builder.Services.AddScoped<ICandleRepository, EfCandleRepository>();
    builder.Services.AddScoped<EfExperimentWorkerRepository>();
    builder.Services.AddSingleton<ScopedExperimentWorkerRepository>();
    builder.Services.AddSingleton<IExperimentWorkerRepository>(provider => provider.GetRequiredService<ScopedExperimentWorkerRepository>());
    builder.Services.AddSingleton<IPaperTradingLedgerRepository>(provider => provider.GetRequiredService<ScopedExperimentWorkerRepository>());
    builder.Services.AddScoped<EfExperimentDecisionLedger>();
    builder.Services.AddScoped<EfExperimentPaperExecutionLedger>();
    builder.Services.AddSingleton<IExperimentCandleSeriesSource, ScopedDurableCandleSource>();
    builder.Services.AddSingleton<ApprovedExperimentStrategyRegistry>(_ => ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
    builder.Services.AddSingleton<IExperimentResearchGroupConfigurationSource, PaperTrainingConfigurationSource>();
    builder.Services.AddSingleton<IExperimentDecisionLedger, ScopedDecisionLedger>();
    builder.Services.AddSingleton<PaperExperimentWorkerRunner>();
    builder.Services.AddSingleton<ExperimentDecisionPolicy>();
    builder.Services.AddSingleton<IPaperTrainingSizingSnapshotSource, DurablePaperTrainingSizingSnapshotSource>();
    builder.Services.AddSingleton<IExperimentPaperExecutionLedger, ScopedPaperExecutionLedger>();
    builder.Services.AddSingleton<PaperExecutionAdapter>();
    builder.Services.AddSingleton<IMarketEventRepository, InMemoryMarketEventRepository>();
    builder.Services.AddSingleton<IStrategyDecisionRepository, InMemoryStrategyDecisionRepository>();
    builder.Services.AddSingleton<ITradeIntentRepository, InMemoryTradeIntentRepository>();
    builder.Services.AddSingleton<IRiskEvaluationRepository, InMemoryRiskEvaluationRepository>();
    builder.Services.AddSingleton<IExecutionCommandRepository, InMemoryExecutionCommandRepository>();
    builder.Services.AddSingleton<IPortfolioUpdateRepository, InMemoryPortfolioUpdateRepository>();
    builder.Services.AddSingleton<IAuditEventWriter, InMemoryAuditEventWriter>();
    builder.Services.AddSingleton<ITradingHaltState, InMemoryTradingHaltState>();
    builder.Services.AddSingleton<OrderIdempotencyGuard>();
    builder.Services.AddSingleton<RiskEngine>();
    builder.Services.AddSingleton<TradePipeline>();
    builder.Services.AddSingleton<ExperimentWorkerRiskEvaluator>();
    builder.Services.AddSingleton<PaperExperimentTradeOrchestrator>();
    builder.Services.AddSingleton<IExperimentWorkerRunner, PaperTrainingSizedExecutionRunner>();
}

// Protective exits have an independent, explicitly disabled schedule. This inert evaluator is
// intentional: enabling the schedule alone cannot activate broader experiment training.
builder.Services.Configure<ExperimentProtectiveExitWorkerOptions>(
    builder.Configuration.GetSection(ExperimentProtectiveExitWorkerOptions.SectionName));
builder.Services.AddSingleton<IExperimentProtectiveExitPositionSource, UnconfiguredExperimentProtectiveExitPositionSource>();
builder.Services.AddSingleton<IExperimentProtectiveExitOwnerEvaluator, UnconfiguredExperimentProtectiveExitOwnerEvaluator>();
builder.Services.AddHostedService<ProtectiveExitWorker>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
