using Microsoft.EntityFrameworkCore;
using Trading.Domain.Experiments;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>Owner-scoped durable worker store that replays the immutable paper ledger on every read.</summary>
public sealed class EfExperimentWorkerRepository : IExperimentWorkerRepository, IPaperTradingLedgerRepository
{
    private readonly TradingDbContext _context;
    public EfExperimentWorkerRepository(TradingDbContext context) => _context = context ?? throw new ArgumentNullException(nameof(context));

    public Task<int> CountAsync(Guid userId, CancellationToken cancellationToken = default) =>
        _context.ExperimentWorkers.CountAsync(worker => worker.UserId == userId, cancellationToken);

    public async Task<ExperimentWorker?> GetAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        var worker = await Query(userId).SingleOrDefaultAsync(value => value.Id == workerId, cancellationToken).ConfigureAwait(false);
        return worker is null ? null : ToDomain(worker);
    }

    public async Task<IReadOnlyCollection<ExperimentWorker>> ListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        (await Query(userId).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ToDomain).ToArray();

    public async Task<IReadOnlyCollection<ExperimentWorker>> ListByNamesAsync(
        Guid userId,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var requested = names.Distinct(StringComparer.Ordinal).ToArray();
        if (requested.Length == 0)
            return [];
        return (await Query(userId)
                .Where(worker => requested.Contains(worker.Name))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();
    }

    public async Task SaveAsync(ExperimentWorker worker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var stored = await _context.ExperimentWorkers.SingleOrDefaultAsync(value => value.Id == worker.Id && value.UserId == worker.UserId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            stored = ToPersisted(worker);
            _context.ExperimentWorkers.Add(stored);
        }
        else
            Copy(worker, stored);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(Guid userId, PaperTradingLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (userId == Guid.Empty || entry.WorkerId == Guid.Empty)
            throw new ArgumentException("Owner and worker are required.");
        if (!await _context.ExperimentWorkers.AnyAsync(worker => worker.Id == entry.WorkerId && worker.UserId == userId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Paper ledger entry owner or worker is unknown.");
        var existing = await _context.PaperTradingLedgerEntries.SingleOrDefaultAsync(value => value.Id == entry.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.WorkerId != entry.WorkerId || existing.UserId != userId || existing.Quantity != entry.Quantity
                || existing.ExecutionPrice != entry.ExecutionPrice || existing.Fee != entry.Fee || existing.OccurredAtUtc != entry.OccurredAtUtc
                || !string.Equals(existing.Direction, entry.Direction, StringComparison.Ordinal) || !string.Equals(existing.Symbol, entry.Symbol, StringComparison.Ordinal))
                throw new InvalidOperationException("Paper ledger entry identity conflicts with immutable persisted evidence.");
            return;
        }
        _context.PaperTradingLedgerEntries.Add(ToPersisted(userId, entry));
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<PaperTradingLedgerEntry>> ListAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default) =>
        (await _context.PaperTradingLedgerEntries.AsNoTracking().Where(value => value.UserId == userId && value.WorkerId == workerId)
            .OrderBy(value => value.OccurredAtUtc).ThenBy(value => value.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ToDomain).ToArray();

    private IQueryable<PersistedExperimentWorker> Query(Guid userId) => _context.ExperimentWorkers.AsNoTracking()
        .Where(worker => worker.UserId == userId).Include(worker => worker.LedgerEntries.OrderBy(entry => entry.OccurredAtUtc).ThenBy(entry => entry.Id));

    private static ExperimentWorker ToDomain(PersistedExperimentWorker value) => ExperimentWorker.Replay(value.Id, value.UserId, value.Name, value.StrategyId,
        value.MarketSymbol, value.StartingCash, value.CreatedAtUtc, value.RandomSeed, value.StrategyParameters,
        new(value.MaxAdditionsPerPosition, value.MaxTotalPurchasedQuantity, value.MaxTotalPurchasedNotional, value.MaxPositionQuantity, value.MaxPositionNotional),
        (ExperimentWorkerStatus)value.Status, value.FailureReason, value.LedgerEntries.Select(ToDomain));

    private static PersistedExperimentWorker ToPersisted(ExperimentWorker value)
    {
        var persisted = new PersistedExperimentWorker();
        Copy(value, persisted);
        return persisted;
    }

    private static void Copy(ExperimentWorker source, PersistedExperimentWorker target)
    {
        target.Id = source.Id; target.UserId = source.UserId; target.Name = source.Name; target.StrategyId = source.StrategyId; target.MarketSymbol = source.MarketSymbol;
        target.StartingCash = source.StartingCash; target.CreatedAtUtc = source.CreatedAtUtc; target.RandomSeed = source.RandomSeed; target.StrategyParameters = source.StrategyParameters;
        target.Status = (int)source.Status; target.FailureReason = source.FailureReason;
        target.MaxAdditionsPerPosition = source.PositionControls.MaxAdditionsPerPosition; target.MaxTotalPurchasedQuantity = source.PositionControls.MaxTotalPurchasedQuantity;
        target.MaxTotalPurchasedNotional = source.PositionControls.MaxTotalPurchasedNotional; target.MaxPositionQuantity = source.PositionControls.MaxPositionQuantity;
        target.MaxPositionNotional = source.PositionControls.MaxPositionNotional;
    }

    private static PersistedPaperTradingLedgerEntry ToPersisted(Guid userId, PaperTradingLedgerEntry value) => new()
    {
        Id = value.Id, UserId = userId, WorkerId = value.WorkerId, Symbol = value.Symbol, Quantity = value.Quantity,
        ExecutionPrice = value.ExecutionPrice, Fee = value.Fee, OccurredAtUtc = value.OccurredAtUtc, Direction = value.Direction
    };
    private static PaperTradingLedgerEntry ToDomain(PersistedPaperTradingLedgerEntry value) =>
        new(value.Id, value.WorkerId, value.Symbol, value.Quantity, value.ExecutionPrice, value.Fee, value.OccurredAtUtc, value.Direction);
}
