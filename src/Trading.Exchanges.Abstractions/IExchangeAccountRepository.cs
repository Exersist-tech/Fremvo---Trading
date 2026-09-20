namespace Trading.Exchanges.Abstractions;

public interface IExchangeAccountRepository
{
    Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default);

    Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default);
}
