using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Audit;
using Trading.Domain.Execution;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>
/// Persists paper pipeline stages as immutable, owner-scoped audit rows in the existing
/// AuditEvents table. The durable execution claim remains the authority for deduplication.
/// </summary>
public class EfPaperPipelineRecordRepository<TPayload> : IPipelineRecordRepository<TPayload>
    where TPayload : class
{
    private readonly TradingDbContext _context;
    private readonly PipelineStage _stage;
    private readonly string _action;

    public EfPaperPipelineRecordRepository(TradingDbContext context, PipelineStage stage)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _stage = stage;
        _action = $"PaperPipeline.{stage}";
    }

    public async Task AddAsync(PipelineRecord<TPayload> record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Stage != _stage || record.Context.Mode != TradingMode.Paper)
            throw new InvalidOperationException("Only an attested paper record for this pipeline stage may be saved.");

        _context.AuditEvents.Add(new AuditEvent(
            record.Id, record.Context.UserId, _action, "PaperPipelineRecord",
            record.Id.ToString("D"), record.RecordedAtUtc, null,
            JsonSerializer.Serialize(record), record.Context.CorrelationId));
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PipelineRecord<TPayload>?> GetAsync(
        Guid userId, Guid recordId, CancellationToken cancellationToken = default)
    {
        var row = await Query(userId).SingleOrDefaultAsync(
            value => value.Id == recordId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : Restore(row, userId);
    }

    public async Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListForUserAsync(
        Guid userId, CancellationToken cancellationToken = default) =>
        (await Query(userId).OrderBy(value => value.OccurredAtUtc).ThenBy(value => value.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        .Select(value => Restore(value, userId)).ToArray();

    public async Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListByCorrelationAsync(
        Guid userId, string correlationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation id is required.", nameof(correlationId));
        return (await Query(userId).Where(value => value.CorrelationId == correlationId.Trim())
                .OrderBy(value => value.OccurredAtUtc).ThenBy(value => value.Id)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .Select(value => Restore(value, userId)).ToArray();
    }

    private IQueryable<AuditEvent> Query(Guid userId) => _context.AuditEvents.AsNoTracking()
        .Where(value => value.ActorUserId == userId && value.Action == _action
            && value.TargetType == "PaperPipelineRecord");

    private PipelineRecord<TPayload> Restore(AuditEvent row, Guid userId)
    {
        var record = JsonSerializer.Deserialize<PipelineRecord<TPayload>>(
            row.After ?? throw new InvalidOperationException("Paper pipeline record has no payload."));
        if (record is null || record.Id != row.Id || record.Stage != _stage
            || record.Context.UserId != userId || record.Context.Mode != TradingMode.Paper
            || record.Context.CorrelationId != row.CorrelationId
            || record.RecordedAtUtc != row.OccurredAtUtc)
            throw new InvalidOperationException("Persisted paper pipeline record does not match its audit envelope.");
        return record;
    }
}

public sealed class EfPaperMarketEvents(TradingDbContext context)
    : EfPaperPipelineRecordRepository<MarketEvent>(context, PipelineStage.MarketEvent), IMarketEventRepository;
public sealed class EfPaperStrategyDecisions(TradingDbContext context)
    : EfPaperPipelineRecordRepository<StrategyDecision>(context, PipelineStage.StrategyDecision), IStrategyDecisionRepository;
public sealed class EfPaperTradeIntents(TradingDbContext context)
    : EfPaperPipelineRecordRepository<TradeIntent>(context, PipelineStage.TradeIntent), ITradeIntentRepository;
public sealed class EfPaperRiskEvaluations(TradingDbContext context)
    : EfPaperPipelineRecordRepository<RiskEvaluation>(context, PipelineStage.RiskEvaluation), IRiskEvaluationRepository;
public sealed class EfPaperExecutionCommands(TradingDbContext context)
    : EfPaperPipelineRecordRepository<ExecutionCommand>(context, PipelineStage.ExecutionCommand), IExecutionCommandRepository;
public sealed class EfPaperExecutionResults(TradingDbContext context)
    : EfPaperPipelineRecordRepository<PaperExecutionEvidence>(context, PipelineStage.Execution), IPaperExecutionResultRepository;
public sealed class EfPaperPortfolioUpdates(TradingDbContext context)
    : EfPaperPipelineRecordRepository<PortfolioUpdate>(context, PipelineStage.PortfolioUpdate), IPortfolioUpdateRepository;

public sealed class EfPaperTradeAuditEvidenceReader(TradingDbContext context) : IPaperTradeAuditEvidenceReader
{
    public async Task<IReadOnlyList<string>> ListActionsByCorrelationAsync(
        Guid ownerId, string correlationId, CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || string.IsNullOrWhiteSpace(correlationId)
            || !correlationId.StartsWith("paper-", StringComparison.Ordinal))
            throw new ArgumentException("An owner and paper correlation are required.");
        return await context.AuditEvents.AsNoTracking()
            .Where(row => row.ActorUserId == ownerId && row.CorrelationId == correlationId
                && row.TargetType == "TradePipeline" && row.Action.StartsWith("Trade."))
            .OrderBy(row => row.OccurredAtUtc).ThenBy(row => row.Id)
            .Select(row => row.Action)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
