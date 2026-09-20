namespace Trading.Application.UseCases.Administration;

public sealed record Plan(
    Guid Id,
    string Name,
    int MaxExperimentWorkers,
    bool LiveTradingEnabled,
    bool FuturesEnabled);
