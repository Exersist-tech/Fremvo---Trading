namespace Trading.Domain.Experiments;

public enum ExperimentWorkerStatus
{
    Created = 0,
    Running,
    Paused,
    Completed,
    Failed
}

public sealed class PaperTradingLedgerEntry
{
    public PaperTradingLedgerEntry(
        Guid id,
        Guid workerId,
        string symbol,
        decimal quantity,
        decimal executionPrice,
        decimal fee,
        DateTimeOffset occurredAtUtc,
        string direction)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Ledger entry id is required.", nameof(id));
        }

        if (workerId == Guid.Empty)
        {
            throw new ArgumentException("Worker id is required.", nameof(workerId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (quantity == 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be zero.");
        }

        if (executionPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(executionPrice), "Execution price must be positive.");
        }

        if (fee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fee), "Fee cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(direction))
        {
            throw new ArgumentException("Direction is required.", nameof(direction));
        }

        Id = id;
        WorkerId = workerId;
        Symbol = symbol.Trim();
        Quantity = quantity;
        ExecutionPrice = executionPrice;
        Fee = fee;
        OccurredAtUtc = occurredAtUtc;
        Direction = direction.Trim();
    }

    public Guid Id { get; }

    public Guid WorkerId { get; }

    public string Symbol { get; }

    public decimal Quantity { get; }

    public decimal ExecutionPrice { get; }

    public decimal Fee { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string Direction { get; }
}

/// <summary>
/// Immutable, per-worker limits for experimental paper positions. These values do not configure
/// live or spot trading. Additions are deliberately capped at a small platform ceiling.
/// </summary>
public sealed record ExperimentPaperPositionControls(
    int MaxAdditionsPerPosition,
    decimal MaxTotalPurchasedQuantity,
    decimal MaxTotalPurchasedNotional,
    decimal MaxPositionQuantity,
    decimal MaxPositionNotional)
{
    public const int PlatformMaxAdditionsPerPosition = 5;

    public static ExperimentPaperPositionControls Default { get; } =
        new(3, 100m, 100_000m, 100m, 100_000m);

    public ExperimentPaperPositionControls Validate()
    {
        if (MaxAdditionsPerPosition < 0 || MaxAdditionsPerPosition > PlatformMaxAdditionsPerPosition)
            throw new ArgumentOutOfRangeException(nameof(MaxAdditionsPerPosition),
                $"Additions must be between zero and {PlatformMaxAdditionsPerPosition}.");
        if (MaxTotalPurchasedQuantity <= 0m || MaxTotalPurchasedNotional <= 0m
            || MaxPositionQuantity <= 0m || MaxPositionNotional <= 0m)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalPurchasedQuantity),
                "All paper-position quantity and notional limits must be positive.");
        return this;
    }
}

public sealed class ExperimentWorker
{
    public const int MaxWorkersPerUser = 10;

    private readonly List<PaperTradingLedgerEntry> _ledger = new();
    private readonly ExperimentPaperPositionControls _positionControls;
    private decimal _totalPurchasedQuantity;
    private decimal _totalPurchasedNotional;
    private int _additionCount;
    private decimal? _favorableMarkPrice;

    public ExperimentWorker(
        Guid id,
        Guid userId,
        string name,
        string strategyId,
        string marketSymbol,
        decimal startingCash,
        DateTimeOffset createdAtUtc,
        int randomSeed,
        ExperimentPaperPositionControls? positionControls = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Worker id is required.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Worker name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(strategyId))
        {
            throw new ArgumentException("Strategy id is required.", nameof(strategyId));
        }

        if (string.IsNullOrWhiteSpace(marketSymbol))
        {
            throw new ArgumentException("Market symbol is required.", nameof(marketSymbol));
        }

        if (startingCash <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(startingCash), "Starting cash must be positive.");
        }

        Id = id;
        UserId = userId;
        Name = name.Trim();
        StrategyId = strategyId.Trim();
        MarketSymbol = marketSymbol.Trim();
        StartingCash = startingCash;
        CashBalance = startingCash;
        CreatedAtUtc = createdAtUtc;
        Status = ExperimentWorkerStatus.Created;
        RandomSeed = randomSeed;
        StrategyParameters = "{}";
        _positionControls = (positionControls ?? ExperimentPaperPositionControls.Default).Validate();
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public string Name { get; }

    public string StrategyId { get; }

    public string MarketSymbol { get; }

    public decimal StartingCash { get; }

    public decimal CashBalance { get; private set; }

    public decimal PositionQuantity { get; private set; }

    public decimal AverageEntryPrice { get; private set; }

    public ExperimentPaperPositionControls PositionControls => _positionControls;

    public int AdditionCount => _additionCount;

    public decimal TotalPurchasedQuantity => _totalPurchasedQuantity;

    public decimal TotalPurchasedNotional => _totalPurchasedNotional;

    public decimal? PriorFavorableMarkPrice => _favorableMarkPrice;

    public DateTimeOffset CreatedAtUtc { get; }

    public ExperimentWorkerStatus Status { get; private set; }

    /// <summary>
    /// Fixed seed for this worker's own random stream. It is supplied at construction and never
    /// regenerated, so a worker's run can be reproduced exactly. Workers never share a stream.
    /// </summary>
    public int RandomSeed { get; }

    public decimal RealizedProfitAndLoss { get; private set; }

    /// <summary>
    /// Why the worker failed, if it did. Retained so one worker's fault is diagnosable without
    /// inspecting the other workers.
    /// </summary>
    public string? FailureReason { get; private set; }

    public string StrategyParameters { get; private set; }

    public IReadOnlyCollection<PaperTradingLedgerEntry> Ledger => _ledger.AsReadOnly();

    public static bool CanCreateMoreWorkers(int existingWorkersCount)
    {
        if (existingWorkersCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(existingWorkersCount), "Worker count cannot be negative.");
        }

        return existingWorkersCount < MaxWorkersPerUser;
    }

    public void UpdateStrategyParameters(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            throw new ArgumentException("Strategy parameters are required.", nameof(parametersJson));
        }

        StrategyParameters = parametersJson.Trim();
    }

    public void Start()
    {
        if (Status == ExperimentWorkerStatus.Completed || Status == ExperimentWorkerStatus.Failed)
        {
            throw new InvalidOperationException("A completed or failed worker cannot be started again.");
        }

        Status = ExperimentWorkerStatus.Running;
    }

    public void Pause()
    {
        if (Status != ExperimentWorkerStatus.Running)
        {
            throw new InvalidOperationException("Only a running worker can be paused.");
        }

        Status = ExperimentWorkerStatus.Paused;
    }

    public void Resume()
    {
        if (Status != ExperimentWorkerStatus.Paused)
        {
            throw new InvalidOperationException("Only a paused worker can be resumed.");
        }

        Status = ExperimentWorkerStatus.Running;
    }

    public void Complete()
    {
        if (Status == ExperimentWorkerStatus.Failed)
        {
            throw new InvalidOperationException("A failed worker cannot be completed.");
        }

        Status = ExperimentWorkerStatus.Completed;
    }

    public void Fail(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Failure reason is required.", nameof(reason));
        }

        Status = ExperimentWorkerStatus.Failed;
        FailureReason = reason.Trim();
    }

    /// <summary>
    /// Records a closed, favorable experimental-paper mark. A later position add must consume
    /// this mark, so every add follows independently observed upside rather than averaging down.
    /// </summary>
    public void RecordFavorablePaperMark(decimal markPrice)
    {
        if (Status != ExperimentWorkerStatus.Running)
            throw new InvalidOperationException($"Only a running worker can record a mark. The worker is currently {Status}.");
        if (PositionQuantity <= 0m)
            throw new InvalidOperationException("A favorable mark requires an existing paper position.");
        if (markPrice <= AverageEntryPrice)
            throw new InvalidOperationException("A paper position add requires a prior mark strictly above its average entry price.");

        _favorableMarkPrice = markPrice;
    }

    public void ApplyPaperTrade(
        decimal quantity,
        decimal executionPrice,
        decimal fee,
        string direction,
        DateTimeOffset? occurredAtUtc = null)
    {
        if (Status != ExperimentWorkerStatus.Running)
        {
            throw new InvalidOperationException(
                $"Only a running worker can trade. The worker is currently {Status}.");
        }

        if (executionPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(executionPrice), "Execution price must be positive.");
        }

        if (fee < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fee), "Fee cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(direction))
        {
            throw new ArgumentException("Direction is required.", nameof(direction));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                "Quantity must be positive; use the direction to express buy or sell.");
        }

        var tradeTime = occurredAtUtc ?? DateTimeOffset.UtcNow;
        if (tradeTime.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Paper-trading occurrence time must be UTC.", nameof(occurredAtUtc));
        }

        var directionLower = direction.Trim();

        if (!directionLower.Equals("buy", StringComparison.OrdinalIgnoreCase) &&
            !directionLower.Equals("sell", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Direction must be 'buy' or 'sell'.", nameof(direction));
        }

        var isBuy = directionLower.Equals("buy", StringComparison.OrdinalIgnoreCase);
        var notional = checked(quantity * executionPrice);
        var cashDelta = isBuy ? -(notional + fee) : notional - fee;
        if (CashBalance + cashDelta < 0m)
        {
            throw new InvalidOperationException(
                "Paper-trading cash balance is insufficient for this trade. Experiment workers never borrow.");
        }

        if (isBuy)
        {
            ApplyPaperBuy(quantity, executionPrice, fee, notional);
        }
        else
        {
            if (quantity > PositionQuantity)
            {
                throw new InvalidOperationException("Cannot sell more paper-trading quantity than the worker currently holds.");
            }

            RealizedProfitAndLoss += ((executionPrice - AverageEntryPrice) * quantity) - fee;
            PositionQuantity -= quantity;
            if (PositionQuantity == 0m)
            {
                AverageEntryPrice = 0m;
                _additionCount = 0;
                _favorableMarkPrice = null;
            }
        }

        CashBalance += cashDelta;

        _ledger.Add(new PaperTradingLedgerEntry(
            Guid.NewGuid(),
            Id,
            MarketSymbol,
            quantity,
            executionPrice,
            fee,
            tradeTime,
            directionLower));
    }

    private void ApplyPaperBuy(decimal quantity, decimal executionPrice, decimal fee, decimal notional)
    {
        var previousQuantity = PositionQuantity;
        var nextQuantity = checked(previousQuantity + quantity);
        var nextPurchasedQuantity = checked(_totalPurchasedQuantity + quantity);
        var nextPurchasedNotional = checked(_totalPurchasedNotional + notional);
        var nextPositionNotional = checked(nextQuantity * executionPrice);

        if (nextPurchasedQuantity > _positionControls.MaxTotalPurchasedQuantity)
            throw new InvalidOperationException("Paper buy exceeds this worker's total purchased quantity limit.");
        if (nextPurchasedNotional > _positionControls.MaxTotalPurchasedNotional)
            throw new InvalidOperationException("Paper buy exceeds this worker's total purchased notional limit.");
        if (nextQuantity > _positionControls.MaxPositionQuantity)
            throw new InvalidOperationException("Paper buy exceeds this worker's position quantity limit.");
        if (nextPositionNotional > _positionControls.MaxPositionNotional)
            throw new InvalidOperationException("Paper buy exceeds this worker's position notional limit.");

        if (previousQuantity > 0m)
        {
            if (_additionCount >= _positionControls.MaxAdditionsPerPosition)
                throw new InvalidOperationException("Paper buy exceeds this worker's maximum additions per position.");
            if (_favorableMarkPrice is null || _favorableMarkPrice <= AverageEntryPrice)
                throw new InvalidOperationException("Paper position add requires a prior realized favorable mark.");
            if (executionPrice < AverageEntryPrice)
                throw new InvalidOperationException("Paper position add below average entry is forbidden to prevent averaging down.");
        }

        var totalCost = checked((AverageEntryPrice * previousQuantity) + notional + fee);
        PositionQuantity = nextQuantity;
        AverageEntryPrice = totalCost / nextQuantity;
        _totalPurchasedQuantity = nextPurchasedQuantity;
        _totalPurchasedNotional = nextPurchasedNotional;
        if (previousQuantity > 0m)
        {
            _additionCount++;
            _favorableMarkPrice = null;
        }
    }
}

/// <summary>
/// Every read is scoped to the owning user. There is deliberately no lookup by worker id alone:
/// a worker belonging to another user must be unreachable, not merely refused.
/// </summary>
public interface IExperimentWorkerRepository
{
    Task<ExperimentWorker?> GetAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ExperimentWorker>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<int> CountAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SaveAsync(ExperimentWorker worker, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ledger reads are scoped to the owning user as well as the worker, so one user can never read
/// another user's paper-trading history by guessing a worker id.
/// </summary>
public interface IPaperTradingLedgerRepository
{
    Task AddAsync(Guid userId, PaperTradingLedgerEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PaperTradingLedgerEntry>> ListAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default);
}
