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

public sealed class ExperimentWorker
{
    public const int MaxWorkersPerUser = 10;

    private readonly List<PaperTradingLedgerEntry> _ledger = new();

    public ExperimentWorker(
        Guid id,
        Guid userId,
        string name,
        string strategyId,
        string marketSymbol,
        decimal startingCash,
        DateTimeOffset createdAtUtc)
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
        RandomSeed = Guid.NewGuid();
        StrategyParameters = "{}";
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

    public DateTimeOffset CreatedAtUtc { get; }

    public ExperimentWorkerStatus Status { get; private set; }

    public Guid RandomSeed { get; }

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
    }

    public void ApplyPaperTrade(decimal quantity, decimal executionPrice, decimal fee, string direction)
    {
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

        if (quantity == 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity cannot be zero.");
        }

        var directionLower = direction.Trim();

        if (!directionLower.Equals("buy", StringComparison.OrdinalIgnoreCase) &&
            !directionLower.Equals("sell", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Direction must be 'buy' or 'sell'.", nameof(direction));
        }

        var notional = Math.Abs(quantity) * executionPrice;
        var cashDelta = directionLower.Equals("buy", StringComparison.OrdinalIgnoreCase)
            ? -(notional + fee)
            : +(notional - fee);

        if (directionLower.Equals("buy", StringComparison.OrdinalIgnoreCase))
        {
            var previousQuantity = PositionQuantity;
            PositionQuantity += quantity;
            var weightedAverage = (AverageEntryPrice * previousQuantity) + (executionPrice * quantity);
            AverageEntryPrice = previousQuantity == 0m
                ? executionPrice
                : weightedAverage / PositionQuantity;
        }
        else
        {
            if (quantity > PositionQuantity)
            {
                throw new InvalidOperationException("Cannot sell more paper-trading quantity than the worker currently holds.");
            }

            PositionQuantity -= quantity;
            if (PositionQuantity == 0m)
            {
                AverageEntryPrice = 0m;
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
            DateTimeOffset.UtcNow,
            directionLower));
    }
}

public interface IExperimentWorkerRepository
{
    Task<ExperimentWorker?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ExperimentWorker>> ListAsync(Guid userId, CancellationToken cancellationToken = default);
}

public interface IPaperTradingLedgerRepository
{
    Task AddAsync(PaperTradingLedgerEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PaperTradingLedgerEntry>> ListAsync(Guid workerId, CancellationToken cancellationToken = default);
}
