using Microsoft.EntityFrameworkCore;
using Trading.Exchanges.Abstractions;
using Trading.Infrastructure.Data;
using Trading.Infrastructure.Data.ExchangeAccounts;

namespace Trading.ArchitectureTests;

public sealed class ExchangeRepositoryPersistenceTests
{
    [Fact]
    public async Task EfExchangeAccountRepositoryStoresAndReadsExchangeAccountsAsync()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var dbContext = new TradingDbContext(options);
        var repository = new EfExchangeAccountRepository(dbContext);
        var account = new ExchangeAccount(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ExchangeKind.Binance,
            "Primary",
            "kv/binance/primary",
            DateTimeOffset.UtcNow);

        await repository.AddAsync(account);

        var persisted = await repository.GetByIdAsync(account.Id);

        Assert.NotNull(persisted);
        Assert.Equal(account.DisplayName, persisted!.DisplayName);
        Assert.Equal(ExchangeKind.Binance, persisted.ExchangeKind);
        Assert.Equal(ExchangeAccountStatus.Disconnected, persisted.Status);
    }
}
