namespace Trading.Application.Pipeline;

using System.Collections.Concurrent;
using Trading.Domain.Execution;

/// <summary>
/// Non-durable, in-memory pipeline repository used by paper trading and experiment workers.
/// Reads are always scoped to the owning user, so a caller cannot observe another user's
/// records even if it supplies a record id it does not own.
/// </summary>
/// <remarks>
/// This implementation is intentionally not durable. Live trading requires a persistent
/// store so that reconciliation can recover after a process restart.
/// </remarks>
public class InMemoryPipelineRecordRepository<TPayload> : IPipelineRecordRepository<TPayload>
    where TPayload : class
{
    private readonly ConcurrentDictionary<Guid, PipelineRecord<TPayload>> _records = new();

    public Task AddAsync(PipelineRecord<TPayload> record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);

        if (!_records.TryAdd(record.Id, record))
        {
            throw new InvalidOperationException($"Pipeline record '{record.Id}' has already been stored.");
        }

        return Task.CompletedTask;
    }

    public Task<PipelineRecord<TPayload>?> GetAsync(Guid userId, Guid recordId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_records.TryGetValue(recordId, out var record))
        {
            return Task.FromResult<PipelineRecord<TPayload>?>(null);
        }

        // A record owned by another user is reported as not found, never as forbidden,
        // so a caller cannot probe for the existence of other users' records.
        return Task.FromResult(record.Context.UserId == userId ? record : null);
    }

    public Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyCollection<PipelineRecord<TPayload>> results = _records.Values
            .Where(r => r.Context.UserId == userId)
            .OrderBy(r => r.RecordedAtUtc)
            .ToList();

        return Task.FromResult(results);
    }

    public Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListByCorrelationAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        }

        var trimmed = correlationId.Trim();

        IReadOnlyCollection<PipelineRecord<TPayload>> results = _records.Values
            .Where(r => r.Context.UserId == userId
                && string.Equals(r.Context.CorrelationId, trimmed, StringComparison.Ordinal))
            .OrderBy(r => r.RecordedAtUtc)
            .ToList();

        return Task.FromResult(results);
    }
}

public sealed class InMemoryMarketEventRepository
    : InMemoryPipelineRecordRepository<MarketEvent>, IMarketEventRepository;

public sealed class InMemoryStrategyDecisionRepository
    : InMemoryPipelineRecordRepository<StrategyDecision>, IStrategyDecisionRepository;

public sealed class InMemoryTradeIntentRepository
    : InMemoryPipelineRecordRepository<TradeIntent>, ITradeIntentRepository;

public sealed class InMemoryRiskEvaluationRepository
    : InMemoryPipelineRecordRepository<RiskEvaluation>, IRiskEvaluationRepository;

public sealed class InMemoryExecutionCommandRepository
    : InMemoryPipelineRecordRepository<ExecutionCommand>, IExecutionCommandRepository;

public sealed class InMemoryPortfolioUpdateRepository
    : InMemoryPipelineRecordRepository<PortfolioUpdate>, IPortfolioUpdateRepository;
