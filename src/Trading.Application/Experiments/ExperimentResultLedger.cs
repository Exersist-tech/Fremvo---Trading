using Trading.Domain.Experiments;

namespace Trading.Application.Experiments;

/// <summary>Immutable provenance needed to reproduce an isolated paper experiment result.</summary>
public sealed record ExperimentResultProvenance(
    Guid WorkerId,
    int GroupConfigurationVersion,
    string Group,
    string StrategyId,
    int StrategyVersion,
    string ParametersFingerprint,
    string DatasetFingerprint,
    string ClassifierVersion,
    string GateEvidenceFingerprint,
    int Seed,
    string ReproducibilityIdentity);

/// <summary>
/// A point-in-time, append-only research result. Nullable accounting values mean the source
/// evidence did not contain the value; they are never substituted with zero.
/// </summary>
public sealed record ExperimentResultSnapshot(
    Guid OwnerUserId,
    string SnapshotKey,
    ExperimentResultProvenance Provenance,
    DateTimeOffset EvaluatedAtUtc,
    decimal? Equity,
    decimal Cash,
    decimal PositionQuantity,
    decimal RealizedProfitAndLoss,
    decimal? UnrealizedProfitAndLoss,
    decimal? MaximumDrawdown,
    decimal Fees,
    decimal? Slippage,
    int? RejectedFillCount,
    int? RejectedActionCount,
    decimal? Exposure,
    int? GateFailureCount)
{
    public ExperimentResultSnapshot Validate()
    {
        if (OwnerUserId == Guid.Empty || Provenance.WorkerId == Guid.Empty || string.IsNullOrWhiteSpace(SnapshotKey)
            || SnapshotKey.Length > 128 || EvaluatedAtUtc.Offset != TimeSpan.Zero
            || Provenance.GroupConfigurationVersion < 1 || string.IsNullOrWhiteSpace(Provenance.Group)
            || string.IsNullOrWhiteSpace(Provenance.StrategyId) || Provenance.StrategyVersion < 1
            || string.IsNullOrWhiteSpace(Provenance.ParametersFingerprint) || string.IsNullOrWhiteSpace(Provenance.DatasetFingerprint)
            || string.IsNullOrWhiteSpace(Provenance.ClassifierVersion) || string.IsNullOrWhiteSpace(Provenance.GateEvidenceFingerprint)
            || string.IsNullOrWhiteSpace(Provenance.ReproducibilityIdentity) || Cash < 0m || PositionQuantity < 0m
            || Fees < 0m || (Equity is decimal equity && equity < 0m)
            || (MaximumDrawdown is decimal drawdown && drawdown < 0m)
            || (Slippage is decimal slippage && slippage < 0m)
            || (RejectedFillCount is int rejectedFills && rejectedFills < 0)
            || (RejectedActionCount is int rejectedActions && rejectedActions < 0)
            || (Exposure is decimal exposure && exposure < 0m)
            || (GateFailureCount is int gateFailures && gateFailures < 0))
            throw new ArgumentException("Experiment result snapshot is incomplete or invalid.");
        return this;
    }
}

public enum ExperimentResultWriteResult { Inserted, Duplicate, Conflict }

public interface IExperimentResultLedger
{
    Task<(ExperimentResultWriteResult Result, ExperimentResultSnapshot? Snapshot)> AppendAsync(
        Guid ownerUserId, ExperimentResultSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<ExperimentResultPage> ListAsync(
        Guid ownerUserId, int page, int pageSize, CancellationToken cancellationToken = default);
}

public sealed record ExperimentResultPage(
    IReadOnlyList<ExperimentResultSnapshot> Items,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record ExperimentClosedTradeResult(
    Guid WorkerId,
    string StrategyId,
    string Symbol,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset ClosedAtUtc,
    decimal Quantity,
    decimal AverageEntryPrice,
    decimal AverageExitPrice,
    decimal GrossProfitAndLoss,
    decimal Fees,
    decimal NetProfitAndLoss,
    decimal ReturnPercent,
    long HoldingSeconds,
    int BuyFillCount,
    int SellFillCount,
    int Additions,
    decimal StartingCash,
    decimal EndingCash,
    string? ExitReason,
    decimal AverageBuyFillPrice);

public static class ExperimentClosedTradeResultFactory
{
    public static ExperimentClosedTradeResult Create(ExperimentWorker worker, string? exitReason = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var ordered = worker.Ledger
            .OrderBy(entry => entry.OccurredAtUtc)
            .ThenBy(entry => entry.Id)
            .ToArray();
        var buys = ordered
            .Where(entry => entry.Direction.Equals("buy", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sells = ordered
            .Where(entry => entry.Direction.Equals("sell", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (worker.Status is not (ExperimentWorkerStatus.Completed or ExperimentWorkerStatus.Failed)
            || worker.PositionQuantity != 0m
            || buys.Length == 0
            || sells.Length == 0)
        {
            throw new InvalidOperationException("A closed-trade result requires a completed or failed flat worker with BUY and SELL fills.");
        }

        var boughtQuantity = buys.Sum(entry => entry.Quantity);
        var soldQuantity = sells.Sum(entry => entry.Quantity);
        if (boughtQuantity <= 0m || boughtQuantity != soldQuantity)
            throw new InvalidOperationException("A closed-trade result requires exactly reconciled bought and sold quantities.");

        var buyNotional = buys.Sum(entry => checked(entry.Quantity * entry.ExecutionPrice));
        var sellNotional = sells.Sum(entry => checked(entry.Quantity * entry.ExecutionPrice));
        var fees = ordered.Sum(entry => entry.Fee);
        var entryCost = checked(buyNotional + buys.Sum(entry => entry.Fee));
        var grossProfitAndLoss = checked(sellNotional - buyNotional);
        var netProfitAndLoss = checked(sellNotional - sells.Sum(entry => entry.Fee) - entryCost);
        var openedAtUtc = buys[0].OccurredAtUtc;
        var closedAtUtc = sells[^1].OccurredAtUtc;
        if (openedAtUtc.Offset != TimeSpan.Zero
            || closedAtUtc.Offset != TimeSpan.Zero
            || closedAtUtc < openedAtUtc)
        {
            throw new InvalidOperationException("Closed-trade timestamps must be ordered UTC instants.");
        }

        return new ExperimentClosedTradeResult(
            worker.Id,
            worker.StrategyId,
            worker.MarketSymbol,
            openedAtUtc,
            closedAtUtc,
            boughtQuantity,
            entryCost / boughtQuantity,
            sellNotional / soldQuantity,
            grossProfitAndLoss,
            fees,
            netProfitAndLoss,
            entryCost == 0m ? 0m : netProfitAndLoss / entryCost * 100m,
            checked((long)(closedAtUtc - openedAtUtc).TotalSeconds),
            buys.Length,
            sells.Length,
            Math.Max(0, buys.Length - 1),
            worker.StartingCash,
            worker.CashBalance,
            string.IsNullOrWhiteSpace(exitReason) ? null : exitReason.Trim(),
            buyNotional / boughtQuantity);
    }
}

/// <summary>Creates accounting fields directly from isolated worker and fill evidence.</summary>
public static class ExperimentResultSnapshotFactory
{
    public static ExperimentResultSnapshot Create(
        Guid ownerUserId,
        string snapshotKey,
        ExperimentResultProvenance provenance,
        DateTimeOffset evaluatedAtUtc,
        ExperimentWorker worker,
        decimal? markPrice,
        IReadOnlyCollection<ExperimentPaperFillRecord>? fills,
        int? rejectedActionCount,
        int? gateFailureCount,
        IReadOnlyCollection<decimal>? equityObservations = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(snapshotKey);
        ArgumentNullException.ThrowIfNull(provenance);
        if (ownerUserId != worker.UserId || provenance.WorkerId != worker.Id)
            throw new InvalidOperationException("A result snapshot must use its worker's owner and identity.");
        if (markPrice is decimal mark && mark <= 0m)
            throw new ArgumentOutOfRangeException(nameof(markPrice));

        var fees = worker.Ledger.Sum(entry => entry.Fee);
        decimal? slippage = fills is null ? null : fills.Sum(fill => fill.Slippage);
        var rejectedFills = fills?.Count(fill => fill.Status == ExperimentPaperFillStatus.Rejected);
        decimal? unrealized = markPrice is decimal price
            ? (price - worker.AverageEntryPrice) * worker.PositionQuantity
            : null;
        decimal? exposure = markPrice is decimal exposurePrice ? worker.PositionQuantity * exposurePrice : null;
        decimal? equity = exposure is decimal currentExposure ? worker.CashBalance + currentExposure : null;

        return new ExperimentResultSnapshot(
            ownerUserId, snapshotKey.Trim(), provenance, evaluatedAtUtc, equity, worker.CashBalance,
            worker.PositionQuantity, worker.RealizedProfitAndLoss, unrealized, MaximumDrawdown(equityObservations),
            fees, slippage, rejectedFills, rejectedActionCount, exposure, gateFailureCount).Validate();
    }

    private static decimal? MaximumDrawdown(IReadOnlyCollection<decimal>? observations)
    {
        if (observations is null || observations.Count == 0) return null;
        decimal? peak = null;
        var maximum = 0m;
        foreach (var value in observations)
        {
            if (value < 0m) throw new ArgumentOutOfRangeException(nameof(observations));
            peak = peak is null || value > peak ? value : peak;
            maximum = Math.Max(maximum, peak.Value - value);
        }
        return maximum;
    }
}
