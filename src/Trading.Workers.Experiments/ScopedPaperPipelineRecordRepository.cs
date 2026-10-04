using Microsoft.Extensions.DependencyInjection;
using Trading.Domain.Execution;
using Trading.Application.Pipeline;
using Trading.Risk;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.Workers.Experiments;

public abstract class ScopedPaperPipelineRecordRepository<TPayload>(
    IServiceScopeFactory scopes, PipelineStage stage) : IPipelineRecordRepository<TPayload>
    where TPayload : class
{
    public async Task AddAsync(PipelineRecord<TPayload> record, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        await Repository(scope).AddAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineRecord<TPayload>?> GetAsync(Guid userId, Guid recordId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        return await Repository(scope).GetAsync(userId, recordId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        return await Repository(scope).ListForUserAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListByCorrelationAsync(
        Guid userId, string correlationId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        return await Repository(scope).ListByCorrelationAsync(userId, correlationId, cancellationToken).ConfigureAwait(false);
    }

    private EfPaperPipelineRecordRepository<TPayload> Repository(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<Trading.Infrastructure.Data.TradingDbContext>(), stage);
}

public sealed class ScopedPaperMarketEvents(IServiceScopeFactory scopes)
    : ScopedPaperPipelineRecordRepository<MarketEvent>(scopes, PipelineStage.MarketEvent), IMarketEventRepository;
public sealed class ScopedPaperStrategyDecisions(IServiceScopeFactory scopes)
    : ScopedPaperPipelineRecordRepository<StrategyDecision>(scopes, PipelineStage.StrategyDecision), IStrategyDecisionRepository;
public sealed class ScopedPaperTradeIntents(IServiceScopeFactory scopes)
    : ScopedPaperPipelineRecordRepository<TradeIntent>(scopes, PipelineStage.TradeIntent), ITradeIntentRepository;
public sealed class ScopedPaperRiskEvaluations(IServiceScopeFactory scopes)
    : ScopedPaperPipelineRecordRepository<RiskEvaluation>(scopes, PipelineStage.RiskEvaluation), IRiskEvaluationRepository;
public sealed class ScopedPaperExecutionCommands(IServiceScopeFactory scopes)
    : ScopedPaperPipelineRecordRepository<ExecutionCommand>(scopes, PipelineStage.ExecutionCommand), IExecutionCommandRepository;
public sealed class ScopedPaperExecutionResults(IServiceScopeFactory scopes)
    : ScopedPaperPipelineRecordRepository<PaperExecutionEvidence>(scopes, PipelineStage.Execution), IPaperExecutionResultRepository;
public sealed class ScopedPaperPortfolioUpdates(IServiceScopeFactory scopes)
    : ScopedPaperPipelineRecordRepository<PortfolioUpdate>(scopes, PipelineStage.PortfolioUpdate), IPortfolioUpdateRepository;

public sealed class ScopedPaperTradingHaltState(IServiceScopeFactory scopes) : ITradingHaltState
{
    public async Task<TradingModeFlags> GetAsync(
        PipelineContext context, string symbol, Guid strategyId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Trading.Infrastructure.Data.TradingDbContext>();
        return await new EfAuditTradingHaltState(db).GetAsync(context, symbol, strategyId, cancellationToken)
            .ConfigureAwait(false);
    }
}
