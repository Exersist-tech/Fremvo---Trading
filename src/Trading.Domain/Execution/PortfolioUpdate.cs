namespace Trading.Domain.Execution;

/// <summary>
/// The portfolio effect of a completed execution: the final stage of the trade pipeline
/// before the audit event is written. Amounts are always decimal and times are UTC.
/// </summary>
public sealed class PortfolioUpdate
{
    public PortfolioUpdate(
        Guid id,
        Guid executionCommandId,
        string symbol,
        decimal positionQuantityBefore,
        decimal positionQuantityAfter,
        decimal cashBalanceBefore,
        decimal cashBalanceAfter,
        decimal realizedPnL,
        decimal fees,
        DateTimeOffset appliedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Portfolio update id is required.", nameof(id));
        }

        if (executionCommandId == Guid.Empty)
        {
            throw new ArgumentException("Execution command id is required.", nameof(executionCommandId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        if (fees < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fees), "Fees cannot be negative.");
        }

        if (cashBalanceBefore < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(cashBalanceBefore), "Cash balance cannot be negative.");
        }

        if (cashBalanceAfter < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(cashBalanceAfter), "Cash balance cannot be negative.");
        }

        Id = id;
        ExecutionCommandId = executionCommandId;
        Symbol = symbol.Trim();
        PositionQuantityBefore = positionQuantityBefore;
        PositionQuantityAfter = positionQuantityAfter;
        CashBalanceBefore = cashBalanceBefore;
        CashBalanceAfter = cashBalanceAfter;
        RealizedPnL = realizedPnL;
        Fees = fees;
        AppliedAtUtc = appliedAtUtc;
    }

    public Guid Id { get; }

    public Guid ExecutionCommandId { get; }

    public string Symbol { get; }

    public decimal PositionQuantityBefore { get; }

    public decimal PositionQuantityAfter { get; }

    public decimal CashBalanceBefore { get; }

    public decimal CashBalanceAfter { get; }

    public decimal RealizedPnL { get; }

    public decimal Fees { get; }

    public DateTimeOffset AppliedAtUtc { get; }

    /// <summary>Signed change in position size. Negative values reduce exposure.</summary>
    public decimal PositionDelta => PositionQuantityAfter - PositionQuantityBefore;

    /// <summary>True when the update strictly reduces absolute exposure.</summary>
    public bool ReducesExposure => Math.Abs(PositionQuantityAfter) < Math.Abs(PositionQuantityBefore);
}
