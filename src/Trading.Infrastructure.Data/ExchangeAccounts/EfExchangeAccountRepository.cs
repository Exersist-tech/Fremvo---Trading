using Microsoft.EntityFrameworkCore;
using Trading.Exchanges.Abstractions;

namespace Trading.Infrastructure.Data.ExchangeAccounts;

public sealed class EfExchangeAccountRepository : IExchangeAccountRepository
{
    private readonly TradingDbContext _dbContext;

    public EfExchangeAccountRepository(TradingDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbContext.ExchangeAccounts
            .SingleOrDefaultAsync(account => account.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var accounts = await _dbContext.ExchangeAccounts
            .Where(account => account.UserId == userId)
            .OrderBy(account => account.DisplayName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return accounts;
    }

    public async Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        _dbContext.ExchangeAccounts.Add(account);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        _dbContext.ExchangeAccounts.Update(account);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
