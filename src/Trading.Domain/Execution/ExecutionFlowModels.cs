namespace Trading.Domain.Execution;

public enum SignalDirection
{
    Hold = 0,
    Buy,
    Sell
}

public enum TradeDirection
{
    Buy = 0,
    Sell
}

public sealed class MarketEvent
{
    public MarketEvent(
        Guid id,
        string symbol,
        Market.CandleInterval interval,
        DateTimeOffset eventTimeUtc,
        decimal lastPrice,
        decimal volume,
        bool isClosed,
        IReadOnlyCollection<string>? qualityFlags = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Market event id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (lastPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(lastPrice), "Last price must be positive.");
        }

        if (volume < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume cannot be negative.");
        }

        Id = id;
        Symbol = symbol.Trim();
        Interval = interval;
        EventTimeUtc = eventTimeUtc;
        LastPrice = lastPrice;
        Volume = volume;
        IsClosed = isClosed;
        QualityFlags = qualityFlags ?? Array.Empty<string>();
    }

    public Guid Id { get; }

    public string Symbol { get; }

    public Market.CandleInterval Interval { get; }

    public DateTimeOffset EventTimeUtc { get; }

    public decimal LastPrice { get; }

    public decimal Volume { get; }

    public bool IsClosed { get; }

    public IReadOnlyCollection<string> QualityFlags { get; }
}

public sealed class StrategyDecision
{
    public StrategyDecision(
        Guid id,
        Guid strategyId,
        string symbol,
        SignalDirection direction,
        decimal confidence,
        DateTimeOffset evaluatedAtUtc,
        string rationale)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Strategy decision id is required.", nameof(id));
        }

        if (strategyId == Guid.Empty)
        {
            throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (confidence < 0m || confidence > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between 0 and 1.");
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("Rationale is required.", nameof(rationale));
        }

        Id = id;
        StrategyId = strategyId;
        Symbol = symbol.Trim();
        Direction = direction;
        Confidence = confidence;
        EvaluatedAtUtc = evaluatedAtUtc;
        Rationale = rationale.Trim();
    }

    public Guid Id { get; }

    public Guid StrategyId { get; }

    public string Symbol { get; }

    public SignalDirection Direction { get; }

    public decimal Confidence { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }

    public string Rationale { get; }
}

public sealed class TradeIntent
{
    public TradeIntent(
        Guid id,
        Guid strategyId,
        string symbol,
        TradeDirection direction,
        decimal quantity,
        decimal limitPrice,
        DateTimeOffset createdAtUtc,
        bool reduceOnly = false,
        bool closeOnly = false)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Trade intent id is required.", nameof(id));
        }

        if (strategyId == Guid.Empty)
        {
            throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (limitPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(limitPrice), "Limit price must be positive.");
        }

        Id = id;
        StrategyId = strategyId;
        Symbol = symbol.Trim();
        Direction = direction;
        Quantity = quantity;
        LimitPrice = limitPrice;
        CreatedAtUtc = createdAtUtc;
        ReduceOnly = reduceOnly;
        CloseOnly = closeOnly;
    }

    public Guid Id { get; }

    public Guid StrategyId { get; }

    public string Symbol { get; }

    public TradeDirection Direction { get; }

    public decimal Quantity { get; }

    public decimal LimitPrice { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public bool ReduceOnly { get; }

    public bool CloseOnly { get; }
}

public sealed class RiskEvaluation
{
    public RiskEvaluation(
        Guid id,
        Guid tradeIntentId,
        bool isAllowed,
        decimal proposedExposure,
        decimal maxAllowedExposure,
        DateTimeOffset evaluatedAtUtc,
        string? reason = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Risk evaluation id is required.", nameof(id));
        }

        if (tradeIntentId == Guid.Empty)
        {
            throw new ArgumentException("Trade intent id is required.", nameof(tradeIntentId));
        }

        if (proposedExposure < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(proposedExposure), "Proposed exposure cannot be negative.");
        }

        if (maxAllowedExposure < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAllowedExposure), "Maximum allowed exposure cannot be negative.");
        }

        Id = id;
        TradeIntentId = tradeIntentId;
        IsAllowed = isAllowed;
        ProposedExposure = proposedExposure;
        MaxAllowedExposure = maxAllowedExposure;
        EvaluatedAtUtc = evaluatedAtUtc;
        Reason = reason;
    }

    public Guid Id { get; }

    public Guid TradeIntentId { get; }

    public bool IsAllowed { get; }

    public decimal ProposedExposure { get; }

    public decimal MaxAllowedExposure { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }

    public string? Reason { get; }
}

public sealed class ExecutionCommand
{
    public ExecutionCommand(
        Guid id,
        Guid tradeIntentId,
        string symbol,
        TradeDirection direction,
        decimal quantity,
        decimal price,
        DateTimeOffset createdAtUtc,
        string clientOrderId,
        bool isPaperOnly = true,
        Guid? exchangeAccountId = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Execution command id is required.", nameof(id));
        }

        if (tradeIntentId == Guid.Empty)
        {
            throw new ArgumentException("Trade intent id is required.", nameof(tradeIntentId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            throw new ArgumentException("Client order id is required.", nameof(clientOrderId));
        }

        // A command that may reach a venue must name the account it will be
        // sent with. Without it the adapter would have to infer whose money is
        // at risk, and an inference is not an acceptable basis for a real
        // order. Simulated commands need no account, because nothing is sent.
        if (!isPaperOnly && (exchangeAccountId is null || exchangeAccountId == Guid.Empty))
        {
            throw new ArgumentException(
                "A command that is not paper-only must name the exchange account it executes against.",
                nameof(exchangeAccountId));
        }

        Id = id;
        TradeIntentId = tradeIntentId;
        Symbol = symbol.Trim();
        Direction = direction;
        Quantity = quantity;
        Price = price;
        CreatedAtUtc = createdAtUtc;
        ClientOrderId = clientOrderId.Trim();
        IsPaperOnly = isPaperOnly;
        ExchangeAccountId = exchangeAccountId;
    }

    public Guid Id { get; }

    public Guid TradeIntentId { get; }

    public string Symbol { get; }

    public TradeDirection Direction { get; }

    public decimal Quantity { get; }

    public decimal Price { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public string ClientOrderId { get; }

    public bool IsPaperOnly { get; }

    /// <summary>
    /// The exchange account this command executes against. Null only for a
    /// paper-only command, which reaches no venue.
    /// </summary>
    public Guid? ExchangeAccountId { get; }
}

/// <summary>
/// What an execution adapter established about a command.
/// </summary>
/// <remarks>
/// <see cref="Rejected"/> and <see cref="Unknown"/> must never be collapsed
/// into a single "did not succeed". A rejection proves no order exists, so a
/// corrected resubmission is safe. An unknown outcome proves nothing, and
/// resubmitting on it is how a network timeout becomes two live positions.
/// </remarks>
public enum ExecutionOutcome
{
    /// <summary>
    /// The exchange accepted the order. This is not a fill; fills must be
    /// observed from the exchange before a position is created.
    /// </summary>
    Accepted = 0,

    /// <summary>
    /// A simulated execution filled, wholly or partly. A real spot execution
    /// adapter must never use this value for an order merely accepted by a
    /// venue.
    /// </summary>
    Filled = 1,

    /// <summary>
    /// The venue refused the order on its own content. No order exists and
    /// none can arise from this command.
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// Nothing was established. The order may be live. It must be reconciled
    /// against the venue before anything further is submitted for the same
    /// intent.
    /// </summary>
    Unknown = 3
}

public interface IExecutionAdapter
{
    Task<ExecutionResult> ExecuteAsync(ExecutionCommand command, CancellationToken cancellationToken = default);
}

public sealed class ExecutionResult
{
    public ExecutionResult(
        Guid executionCommandId,
        bool success,
        string status,
        decimal filledQuantity,
        decimal averageFillPrice,
        decimal fees,
        DateTimeOffset executedAtUtc,
        string? failureReason = null,
        ExecutionOutcome? outcome = null)
    {
        if (executionCommandId == Guid.Empty)
        {
            throw new ArgumentException("Execution command id is required.", nameof(executionCommandId));
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Status is required.", nameof(status));
        }

        if (filledQuantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(filledQuantity), "Filled quantity cannot be negative.");
        }

        if (averageFillPrice < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(averageFillPrice), "Average fill price cannot be negative.");
        }

        if (fees < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fees), "Fees cannot be negative.");
        }

        ExecutionCommandId = executionCommandId;
        Success = success;
        Status = status.Trim();
        FilledQuantity = filledQuantity;
        AverageFillPrice = averageFillPrice;
        Fees = fees;
        ExecutedAtUtc = executedAtUtc;
        FailureReason = failureReason;

        // Deriving the outcome is only legitimate for an adapter that cannot
        // be in doubt — a simulated fill always knows what it did. A venue
        // adapter must state the outcome, because "did not succeed" is exactly
        // where a refusal and an unanswered request look alike.
        Outcome = outcome ?? (success ? ExecutionOutcome.Filled : ExecutionOutcome.Rejected);
    }

    /// <summary>
    /// What was established. Prefer this over <see cref="Success"/> when
    /// deciding whether anything further may be submitted.
    /// </summary>
    public ExecutionOutcome Outcome { get; }

    /// <summary>
    /// True when the platform must establish the venue's state before
    /// submitting anything else for the same intent.
    /// </summary>
    public bool RequiresReconciliation => Outcome == ExecutionOutcome.Unknown;

    public Guid ExecutionCommandId { get; }

    public bool Success { get; }

    public string Status { get; }

    public decimal FilledQuantity { get; }

    public decimal AverageFillPrice { get; }

    public decimal Fees { get; }

    public DateTimeOffset ExecutedAtUtc { get; }

    public string? FailureReason { get; }
}

public sealed class PaperExecutionLedgerEntry
{
    public PaperExecutionLedgerEntry(
        Guid id,
        Guid executionCommandId,
        string symbol,
        TradeDirection direction,
        decimal quantity,
        decimal price,
        decimal fees,
        DateTimeOffset executedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Ledger id is required.", nameof(id));
        }

        if (executionCommandId == Guid.Empty)
        {
            throw new ArgumentException("Execution command id is required.", nameof(executionCommandId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        if (fees < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fees), "Fees cannot be negative.");
        }

        Id = id;
        ExecutionCommandId = executionCommandId;
        Symbol = symbol.Trim();
        Direction = direction;
        Quantity = quantity;
        Price = price;
        Fees = fees;
        ExecutedAtUtc = executedAtUtc;
    }

    public Guid Id { get; }

    public Guid ExecutionCommandId { get; }

    public string Symbol { get; }

    public TradeDirection Direction { get; }

    public decimal Quantity { get; }

    public decimal Price { get; }

    public decimal Fees { get; }

    public DateTimeOffset ExecutedAtUtc { get; }
}

public sealed class PaperExecutionAdapter : IExecutionAdapter
{
    private readonly List<PaperExecutionLedgerEntry> _ledger = new();

    public IReadOnlyCollection<PaperExecutionLedgerEntry> Ledger => _ledger.AsReadOnly();

    public Task<ExecutionResult> ExecuteAsync(ExecutionCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentNullException.ThrowIfNull(command);

        if (!command.IsPaperOnly)
        {
            return Task.FromResult(new ExecutionResult(
                command.Id,
                false,
                "Rejected",
                0m,
                0m,
                0m,
                DateTimeOffset.UtcNow,
                "This adapter is paper-only and cannot execute live trading commands."));
        }

        var fees = command.Quantity * command.Price * 0.0005m;
        var result = new ExecutionResult(
            command.Id,
            true,
            "Filled",
            command.Quantity,
            command.Price,
            fees,
            DateTimeOffset.UtcNow);

        _ledger.Add(new PaperExecutionLedgerEntry(
            Guid.NewGuid(),
            command.Id,
            command.Symbol,
            command.Direction,
            command.Quantity,
            command.Price,
            fees,
            result.ExecutedAtUtc));

        return Task.FromResult(result);
    }
}
