using System.Collections.ObjectModel;

namespace Trading.Infrastructure.Data.Experiments;

public sealed class PersistedExperimentWorker
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public string MarketSymbol { get; set; } = string.Empty;
    public decimal StartingCash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int RandomSeed { get; set; }
    public string StrategyParameters { get; set; } = "{}";
    public int Status { get; set; }
    public string? FailureReason { get; set; }
    public int MaxAdditionsPerPosition { get; set; }
    public decimal MaxTotalPurchasedQuantity { get; set; }
    public decimal MaxTotalPurchasedNotional { get; set; }
    public decimal MaxPositionQuantity { get; set; }
    public decimal MaxPositionNotional { get; set; }
    public Collection<PersistedPaperTradingLedgerEntry> LedgerEntries { get; } = [];
}

public sealed class PersistedPaperTradingLedgerEntry
{
    public Guid Id { get; set; }
    public Guid WorkerId { get; set; }
    public Guid UserId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal ExecutionPrice { get; set; }
    public decimal Fee { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string Direction { get; set; } = string.Empty;
}
