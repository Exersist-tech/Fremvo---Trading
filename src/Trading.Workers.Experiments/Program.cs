using Trading.Application.Experiments;
using Trading.Application.Pipeline;
using Trading.Application.UseCases.Audit;
using Trading.Domain.Experiments;
using Trading.Domain.Execution;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.Experiments;
using Trading.Infrastructure.Data.Audit;
using Trading.Infrastructure.Data.MarketData;
using Trading.MarketData.Experiments;
using Trading.MarketData;
using Microsoft.EntityFrameworkCore;
using Trading.Workers.Experiments;
using Trading.Risk;
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
}
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));

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
builder.Services.AddSingleton<IPaperTrainingProtectionOwnerSource, DisabledPaperTrainingActivationSource>();
var paperTrainingEnabled = builder.Configuration.GetValue<bool>("Experiments:PaperTraining:Enabled");
if (paperTrainingEnabled)
{
    if (!builder.Configuration.GetValue<bool>("Experiments:ProtectiveExits:Enabled"))
    {
        throw new InvalidOperationException(
            "Paper training requires Experiments:ProtectiveExits:Enabled so every active owner has protective-exit evaluation.");
    }

    var connectionString = builder.Configuration.GetConnectionString("TradingDb");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Experiments:PaperTraining:Enabled requires ConnectionStrings:TradingDb; no paper workers were started.");
    }

    builder.Services.AddDbContext<TradingDbContext>(options => options.UseSqlServer(connectionString));
    builder.Services.AddScoped<IEntitlementRepository, EfEntitlementRepository>();
    builder.Services.AddSingleton<IPaperWorkerAdmissionLimit>(provider =>
        new ScopedPaperWorkerAdmissionLimit(
            provider.GetRequiredService<IServiceScopeFactory>(),
            builder.Configuration.GetValue<bool>("Entitlements:PaperWorkerLimitsEnabled")));
    builder.Services.AddHostedService<PaperHostSchemaReadiness>();
    builder.Services.AddScoped<EfPaperTrainingActivationRepository>();
    builder.Services.AddScoped<IPaperTrainingActivationRepository>(provider =>
        provider.GetRequiredService<EfPaperTrainingActivationRepository>());
    builder.Services.AddScoped<IPaperTrainingActivationSource>(provider =>
        provider.GetRequiredService<EfPaperTrainingActivationRepository>());
    builder.Services.AddSingleton<ScopedPaperTrainingActivationSource>();
    builder.Services.AddSingleton<IPaperTrainingActivationSource>(provider =>
        provider.GetRequiredService<ScopedPaperTrainingActivationSource>());
    builder.Services.AddSingleton<IPaperTrainingProtectionOwnerSource>(provider =>
        provider.GetRequiredService<ScopedPaperTrainingActivationSource>());
    builder.Services.AddSingleton<IPaperTrainingActivationReader, ScopedPaperTrainingActivationReader>();
    builder.Services.AddScoped<ICandleRepository, EfCandleRepository>();
    builder.Services.AddScoped<EfExperimentWorkerRepository>();
    builder.Services.AddSingleton<ScopedExperimentWorkerRepository>();
    builder.Services.AddSingleton<IExperimentWorkerRepository>(provider => provider.GetRequiredService<ScopedExperimentWorkerRepository>());
    builder.Services.AddSingleton<IPaperTradingLedgerRepository>(provider => provider.GetRequiredService<ScopedExperimentWorkerRepository>());
    builder.Services.AddScoped<EfExperimentDecisionLedger>();
    builder.Services.AddScoped<EfExperimentPaperExecutionLedger>();
    builder.Services.AddScoped<EfExperimentPaperPlanEvidenceRepository>();
    builder.Services.AddScoped<EfAuditEventWriter>();
    builder.Services.AddSingleton<IExperimentCandleSeriesSource, ScopedDurableCandleSource>();
    builder.Services.AddSingleton<ApprovedExperimentStrategyRegistry>(_ => ApprovedExperimentStrategyRegistry.CreatePlatformDefault());
    // Supplemental multi-input evidence is deliberately registered only on the explicit durable
    // paper-training path. The default host provider remains fail-closed.
    builder.Services.AddSingleton<ISupplementalExperimentEvidenceProvider, PlatformSupplementalExperimentEvidenceProvider>();
    builder.Services.AddSingleton<IExperimentResearchGroupConfigurationSource, PaperTrainingConfigurationSource>();
    builder.Services.AddSingleton<IExperimentDecisionLedger, ScopedDecisionLedger>();
    builder.Services.AddSingleton<PaperExperimentWorkerRunner>();
    builder.Services.AddSingleton<ExperimentDecisionPolicy>();
    builder.Services.AddSingleton<IPaperTrainingSizingSnapshotSource, DurablePaperTrainingSizingSnapshotSource>();
    builder.Services.AddSingleton<IExperimentPaperExecutionLedger, ScopedPaperExecutionLedger>();
    builder.Services.AddSingleton<IExperimentPaperPlanEvidenceRepository, ScopedPaperPlanEvidenceRepository>();
    builder.Services.AddSingleton<PaperExecutionAdapter>();
    builder.Services.AddSingleton<IMarketEventRepository, ScopedPaperMarketEvents>();
    builder.Services.AddSingleton<IStrategyDecisionRepository, ScopedPaperStrategyDecisions>();
    builder.Services.AddSingleton<ITradeIntentRepository, ScopedPaperTradeIntents>();
    builder.Services.AddSingleton<IRiskEvaluationRepository, ScopedPaperRiskEvaluations>();
    builder.Services.AddSingleton<IExecutionCommandRepository, ScopedPaperExecutionCommands>();
    builder.Services.AddSingleton<IPaperExecutionResultRepository, ScopedPaperExecutionResults>();
    builder.Services.AddSingleton<IPortfolioUpdateRepository, ScopedPaperPortfolioUpdates>();
    builder.Services.AddSingleton<IAuditEventWriter, ScopedPaperTrainingAuditWriter>();
    builder.Services.AddSingleton<ITradingHaltState, ScopedPaperTradingHaltState>();
    builder.Services.AddSingleton<OrderIdempotencyGuard>();
    builder.Services.AddSingleton<RiskEngine>();
    builder.Services.AddSingleton<TradePipeline>();
    builder.Services.AddSingleton<ExperimentWorkerRiskEvaluator>();
    builder.Services.AddSingleton<PaperExperimentTradeOrchestrator>(provider =>
        new PaperExperimentTradeOrchestrator(
            provider.GetRequiredService<IExperimentDecisionLedger>(),
            provider.GetRequiredService<IExperimentPaperExecutionLedger>(),
            provider.GetRequiredService<TradePipeline>(),
            provider.GetRequiredService<PaperExecutionAdapter>(),
            workerLedger: provider.GetRequiredService<IPaperTradingLedgerRepository>(),
            workers: provider.GetRequiredService<IExperimentWorkerRepository>()));
    builder.Services.AddSingleton<IExperimentWorkerRunner, PaperTrainingSizedExecutionRunner>();
    builder.Services.AddSingleton<IExperimentProtectiveExitPositionSource, DurablePaperTrainingProtectiveExitPositionSource>();
    builder.Services.AddSingleton<IExperimentProtectiveExitLedger, DurablePaperTrainingProtectiveExitLedger>();
    builder.Services.AddSingleton<IExperimentProtectiveExitOwnerEvaluator, ExperimentProtectiveExitOrchestrator>();
    builder.Services.AddHostedService<PaperHostHeartbeatWorker>();
}

// Protective exits have an independent, explicitly disabled schedule. This inert evaluator is
// intentional: enabling the schedule alone cannot activate broader experiment training.
builder.Services.Configure<ExperimentProtectiveExitWorkerOptions>(
    builder.Configuration.GetSection(ExperimentProtectiveExitWorkerOptions.SectionName));
if (!paperTrainingEnabled)
{
    builder.Services.AddSingleton<IExperimentProtectiveExitPositionSource, UnconfiguredExperimentProtectiveExitPositionSource>();
    builder.Services.AddSingleton<IExperimentProtectiveExitOwnerEvaluator, UnconfiguredExperimentProtectiveExitOwnerEvaluator>();
}
builder.Services.AddHostedService<ProtectiveExitWorker>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
