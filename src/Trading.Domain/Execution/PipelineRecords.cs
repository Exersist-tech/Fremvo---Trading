namespace Trading.Domain.Execution;

/// <summary>
/// Distinguishes simulated execution from real-money execution. Every persisted pipeline
/// record carries this so paper and live activity can never be confused in data or UI.
/// </summary>
public enum TradingMode
{
    Paper = 0,
    Live
}

/// <summary>
/// Ordered stages of the mandatory trade pipeline. A trade may only progress through these
/// stages in order; strategies never reach an exchange adapter directly.
/// </summary>
public enum PipelineStage
{
    MarketEvent = 0,
    StrategyDecision,
    TradeIntent,
    RiskEvaluation,
    ExecutionCommand,
    Execution,
    Reconciliation,
    PortfolioUpdate,
    AuditEvent
}

/// <summary>
/// Ownership and correlation metadata attached to every pipeline record.
/// The owning user is mandatory: it is the isolation boundary enforced by every repository.
/// </summary>
public sealed class PipelineContext
{
    public PipelineContext(
        Guid userId,
        TradingMode mode,
        string correlationId,
        Guid? experimentWorkerId = null)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("Owning user id is required for every pipeline record.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation id is required to trace a trade end to end.", nameof(correlationId));
        }

        if (experimentWorkerId == Guid.Empty)
        {
            throw new ArgumentException("Experiment worker id cannot be an empty guid.", nameof(experimentWorkerId));
        }

        UserId = userId;
        Mode = mode;
        CorrelationId = correlationId.Trim();
        ExperimentWorkerId = experimentWorkerId;
    }

    public Guid UserId { get; }

    public TradingMode Mode { get; }

    /// <summary>Ties every stage of one trade together for audit and reconciliation.</summary>
    public string CorrelationId { get; }

    /// <summary>Set when the record belongs to an isolated experiment worker.</summary>
    public Guid? ExperimentWorkerId { get; }

    public bool IsPaper => Mode == TradingMode.Paper;
}

/// <summary>
/// A persisted pipeline record. The envelope guarantees that ownership, trading mode, and
/// correlation are always stored alongside the payload.
/// </summary>
public sealed class PipelineRecord<TPayload>
    where TPayload : class
{
    public PipelineRecord(
        Guid id,
        PipelineContext context,
        PipelineStage stage,
        TPayload payload,
        DateTimeOffset recordedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Pipeline record id is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);

        Id = id;
        Context = context;
        Stage = stage;
        Payload = payload;
        RecordedAtUtc = recordedAtUtc;
    }

    public Guid Id { get; }

    public PipelineContext Context { get; }

    public PipelineStage Stage { get; }

    public TPayload Payload { get; }

    public DateTimeOffset RecordedAtUtc { get; }
}

/// <summary>
/// Repository port for one pipeline stage. Every read is scoped to an owning user; there is
/// deliberately no unscoped "list all" method, so cross-user reads cannot be written by accident.
/// </summary>
public interface IPipelineRecordRepository<TPayload>
    where TPayload : class
{
    Task AddAsync(PipelineRecord<TPayload> record, CancellationToken cancellationToken = default);

    Task<PipelineRecord<TPayload>?> GetAsync(Guid userId, Guid recordId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PipelineRecord<TPayload>>> ListByCorrelationAsync(
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public interface IMarketEventRepository : IPipelineRecordRepository<MarketEvent>;

public interface IStrategyDecisionRepository : IPipelineRecordRepository<StrategyDecision>;

public interface ITradeIntentRepository : IPipelineRecordRepository<TradeIntent>;

public interface IRiskEvaluationRepository : IPipelineRecordRepository<RiskEvaluation>;

public interface IExecutionCommandRepository : IPipelineRecordRepository<ExecutionCommand>;

public interface IPortfolioUpdateRepository : IPipelineRecordRepository<PortfolioUpdate>;
