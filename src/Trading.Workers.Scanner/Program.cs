using Trading.Application.Universe;
using Trading.Domain.Universe;
using Trading.Workers.Scanner;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new UniverseRecalculationOptions());
builder.Services.AddSingleton<IUniverseWorkSource, UnconfiguredUniverseWorkSource>();
builder.Services.AddSingleton<IUniverseEvidenceSource, UnconfiguredUniverseEvidenceSource>();

// The mandatory platform floor. Operator configuration is combined with this
// and may only ever be stricter, never more permissive.
builder.Services.AddSingleton(_ => new EligibilityThresholds(
    minimumRollingQuoteVolume: 5_000_000m,
    minimumMedianQuoteVolume: 3_000_000m,
    maximumSpread: 0.0020m,
    maximumEstimatedSlippage: 0.0035m,
    minimumHistoryCandles: 1_000,
    minimumListingAge: TimeSpan.FromDays(90),
    maximumEvidenceAge: TimeSpan.FromHours(6)));

builder.Services.AddSingleton(provider => new InstrumentEligibilityEvaluator(
    new[] { MarketUniverseSeed.QuoteAsset },
    provider.GetRequiredService<EligibilityThresholds>()));

builder.Services.AddSingleton(provider => new UniverseRecalculationService(
    provider.GetRequiredService<InstrumentEligibilityEvaluator>(),
    new InstrumentDegradationPolicy(provider.GetRequiredService<EligibilityThresholds>().MaximumEvidenceAge),
    NewListingPolicy.PlatformFloor,
    provider.GetRequiredService<EligibilityThresholds>(),
    provider.GetRequiredService<IUniverseEvidenceSource>(),
    provider.GetRequiredService<TimeProvider>()));

builder.Services.AddHostedService<UniverseRecalculationWorker>();

var host = builder.Build();
host.Run();
