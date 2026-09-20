using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Backtesting;

namespace Trading.Application.Backtesting;

/// <summary>
/// Provides a bounded, read-only projection of completed reproducible backtests.
/// It deliberately does not create, execute, or retain backtests.
/// </summary>
public sealed class BacktestResultsQueryService
{
    public const int PageSize = 20;
    public const int EventLimit = 100;
    public const int EquitySnapshotLimit = 100;

    private readonly IBacktestResultSource _source;

    public BacktestResultsQueryService(IBacktestResultSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async Task<BacktestResultsPage> ListAsync(
        Guid ownerId,
        int page,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("A valid owner is required.", nameof(ownerId));
        }

        var normalizedPage = Math.Max(page, 0);
        var skip = checked(normalizedPage * PageSize);
        var records = await _source
            .ListForOwnerAsync(ownerId, skip, PageSize + 1, cancellationToken)
            .ConfigureAwait(false);

        // Reapply the scope even when a source is expected to enforce it. A
        // presentation read must never disclose a platform or another user's record.
        var scoped = records
            .Where(record => record.OwnerId == ownerId)
            .Take(PageSize + 1)
            .ToArray();

        return new BacktestResultsPage(
            normalizedPage,
            scoped.Length > PageSize,
            scoped.Take(PageSize).Select(ToReport).ToArray());
    }

    private static BacktestResultReport ToReport(BacktestResultEnvelope envelope)
    {
        var result = envelope.Result;
        var events = result.Events.Take(EventLimit).Select(ToEvent).ToArray();
        var snapshots = result.EquitySnapshots.Take(EquitySnapshotLimit).Select(ToSnapshot).ToArray();

        return new BacktestResultReport(
            envelope.Id,
            result.StrategyId,
            envelope.StrategyTemplateVersion,
            result.Symbol,
            Utc(result.FromUtc),
            Utc(result.ToUtc),
            Utc(envelope.AsOfUtc),
            Utc(envelope.CompletedAtUtc),
            result.DatasetVersionIdentity!,
            envelope.Provenance,
            Decimal(result.InitialCapital),
            Decimal(result.FinalPortfolioValue),
            Decimal(result.NetPnL),
            Decimal(result.TotalFees),
            Decimal(result.TotalSlippage),
            result.TradeCount,
            result.Events.Count,
            result.Events.Count(eventItem => eventItem.Action == BacktestSimulatedAction.Rejected),
            events,
            result.Events.Count > events.Length,
            snapshots,
            result.EquitySnapshots.Count > snapshots.Length);
    }

    private static BacktestEventReport ToEvent(BacktestEvent eventItem) =>
        new(
            Utc(eventItem.ObservedAtUtc),
            EventStatus(eventItem.Action),
            eventItem.Rationale,
            Decimal(eventItem.Quantity),
            Decimal(eventItem.Price),
            Decimal(eventItem.Fee),
            Decimal(eventItem.Slippage),
            Decimal(eventItem.CashBalance),
            Decimal(eventItem.BaseQuantity),
            eventItem.ReferencePrice is null ? null : Decimal(eventItem.ReferencePrice.Value));

    private static BacktestEquitySnapshotReport ToSnapshot(BacktestEquitySnapshot snapshot) =>
        new(
            Utc(snapshot.ObservedAtUtc),
            Decimal(snapshot.Equity),
            Decimal(snapshot.CashBalance),
            Decimal(snapshot.BaseQuantity),
            Decimal(snapshot.MarkPrice));

    private static string EventStatus(BacktestSimulatedAction action) => action switch
    {
        BacktestSimulatedAction.None => "Observation recorded",
        BacktestSimulatedAction.Buy => "Simulated entry recorded",
        BacktestSimulatedAction.Sell => "Simulated exit recorded",
        BacktestSimulatedAction.Rejected => "Simulation rejected",
        _ => "Unknown simulation outcome"
    };

    private static string Decimal(decimal value) =>
        value.ToString("G29", CultureInfo.CurrentCulture);

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

public interface IBacktestResultSource
{
    Task<IReadOnlyList<BacktestResultEnvelope>> ListForOwnerAsync(
        Guid ownerId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable completed-backtest evidence supplied by a trusted result store or
/// process. Null ownership is reserved for platform-owned records and is never
/// returned by a user-scoped query.
/// </summary>
public sealed class BacktestResultEnvelope
{
    public BacktestResultEnvelope(
        Guid id,
        Guid? ownerId,
        BacktestResult result,
        string strategyTemplateVersion,
        DateTimeOffset completedAtUtc,
        DateTimeOffset asOfUtc,
        string provenance)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A result id is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(result);
        RequireUtc(result.FromUtc, nameof(result));
        RequireUtc(result.ToUtc, nameof(result));
        RequireUtc(completedAtUtc, nameof(completedAtUtc));
        RequireUtc(asOfUtc, nameof(asOfUtc));

        if (string.IsNullOrWhiteSpace(result.DatasetVersionIdentity))
        {
            throw new ArgumentException("A dataset version identity is required.", nameof(result));
        }

        if (string.IsNullOrWhiteSpace(strategyTemplateVersion))
        {
            throw new ArgumentException("A strategy template version is required.", nameof(strategyTemplateVersion));
        }

        if (string.IsNullOrWhiteSpace(provenance))
        {
            throw new ArgumentException("Provenance is required.", nameof(provenance));
        }

        foreach (var eventItem in result.Events)
        {
            RequireUtc(eventItem.ObservedAtUtc, nameof(result));
            if (string.IsNullOrWhiteSpace(eventItem.Rationale))
            {
                throw new ArgumentException("Each event must include its recorded rationale.", nameof(result));
            }
        }

        foreach (var snapshot in result.EquitySnapshots)
        {
            RequireUtc(snapshot.ObservedAtUtc, nameof(result));
        }

        Id = id;
        OwnerId = ownerId;
        Result = result;
        StrategyTemplateVersion = strategyTemplateVersion.Trim();
        CompletedAtUtc = completedAtUtc;
        AsOfUtc = asOfUtc;
        Provenance = provenance.Trim();
    }

    public Guid Id { get; }
    public Guid? OwnerId { get; }
    public BacktestResult Result { get; }
    public string StrategyTemplateVersion { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public DateTimeOffset AsOfUtc { get; }
    public string Provenance { get; }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamps must be UTC.", parameterName);
        }
    }
}

/// <summary>
/// An immutable supplied-data adapter intended for a future trusted result
/// store. It has no mutation API and does not fabricate records.
/// </summary>
public sealed class SuppliedBacktestResultSource : IBacktestResultSource
{
    private readonly IReadOnlyList<BacktestResultEnvelope> _records;

    public SuppliedBacktestResultSource(IEnumerable<BacktestResultEnvelope> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = new ReadOnlyCollection<BacktestResultEnvelope>(records.ToArray());
    }

    public Task<IReadOnlyList<BacktestResultEnvelope>> ListForOwnerAsync(
        Guid ownerId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || skip < 0 || take is < 1 or > BacktestResultsQueryService.PageSize + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<BacktestResultEnvelope>>(_records
            .Where(record => record.OwnerId == ownerId)
            .Skip(skip)
            .Take(take)
            .ToArray());
    }
}

/// <summary>Default while no durable completed-backtest store has been introduced.</summary>
public sealed class UnavailableBacktestResultSource : IBacktestResultSource
{
    public Task<IReadOnlyList<BacktestResultEnvelope>> ListForOwnerAsync(
        Guid ownerId,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BacktestResultEnvelope>>(Array.Empty<BacktestResultEnvelope>());
}

public sealed record BacktestResultsPage(
    int Page,
    bool HasMore,
    IReadOnlyList<BacktestResultReport> Results);

public sealed record BacktestResultReport(
    Guid Id,
    string StrategyId,
    string StrategyTemplateVersion,
    string Symbol,
    string FromUtc,
    string ToUtc,
    string AsOfUtc,
    string CompletedAtUtc,
    string DatasetVersionIdentity,
    string Provenance,
    string InitialPortfolio,
    string FinalPortfolio,
    string NetPnl,
    string TotalFees,
    string TotalSlippage,
    int TradeCount,
    int EventCount,
    int RejectedEventCount,
    IReadOnlyList<BacktestEventReport> Events,
    bool HasAdditionalEvents,
    IReadOnlyList<BacktestEquitySnapshotReport> EquitySnapshots,
    bool HasAdditionalEquitySnapshots);

public sealed record BacktestEventReport(
    string ObservedAtUtc,
    string Status,
    string Rationale,
    string Quantity,
    string Price,
    string Fee,
    string Slippage,
    string CashBalance,
    string BaseQuantity,
    string? ReferencePrice);

public sealed record BacktestEquitySnapshotReport(
    string ObservedAtUtc,
    string Equity,
    string CashBalance,
    string BaseQuantity,
    string MarkPrice);
