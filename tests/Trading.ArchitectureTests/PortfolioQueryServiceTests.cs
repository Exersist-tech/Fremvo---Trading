using Trading.Application.UseCases.Portfolio;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Account;
using Trading.Infrastructure.Secrets;

namespace Trading.ArchitectureTests;

public sealed class PortfolioQueryServiceTests
{
    [Fact]
    public async Task ReadReturnsOnlyAccountsOwnedByTheRequestingUser()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var accounts = new FakeAccounts(
            ConnectedAccount(owner, "Owner account"),
            ConnectedAccount(other, "Other account"));
        var secrets = new InMemorySecretStore();
        await StoreCredentialsAsync(secrets, accounts.All);
        var service = new PortfolioQueryService(accounts, secrets, [new FixedGateway()]);

        var result = await service.ReadAsync(owner);

        var reading = Assert.Single(result);
        Assert.Equal("Owner account", reading.DisplayName);
        Assert.DoesNotContain(result, item => item.DisplayName == "Other account");
    }

    [Fact]
    public async Task ReadDoesNotReturnStaleBalancesWhenTheExchangeFails()
    {
        var user = Guid.NewGuid();
        var account = ConnectedAccount(user, "Kraken");
        var accounts = new FakeAccounts(account);
        var secrets = new InMemorySecretStore();
        await StoreCredentialsAsync(secrets, accounts.All);
        var service = new PortfolioQueryService(accounts, secrets, [new ThrowingGateway()]);

        var reading = Assert.Single(await service.ReadAsync(user));

        Assert.Null(reading.RetrievedAtUtc);
        Assert.Empty(reading.Balances);
        Assert.Equal("The exchange could not provide a current balance reading.", reading.Error);
    }

    [Fact]
    public async Task ReadNeverExposesCredentialsWhenAReadingFails()
    {
        var user = Guid.NewGuid();
        var account = ConnectedAccount(user, "Kraken");
        var accounts = new FakeAccounts(account);
        var secrets = new InMemorySecretStore();
        await StoreCredentialsAsync(secrets, accounts.All);
        var service = new PortfolioQueryService(accounts, secrets, [new ThrowingGateway()]);

        var serialized = System.Text.Json.JsonSerializer.Serialize(await service.ReadAsync(user));

        Assert.DoesNotContain("secret-value", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("public-key", serialized, StringComparison.Ordinal);
    }

    private static ExchangeAccount ConnectedAccount(Guid userId, string name)
    {
        var account = new ExchangeAccount(Guid.NewGuid(), userId, ExchangeKind.Kraken, name,
            "credentials/" + Guid.NewGuid(), DateTimeOffset.UtcNow);
        account.MarkConnected(DateTimeOffset.UtcNow);
        return account;
    }

    private static async Task StoreCredentialsAsync(InMemorySecretStore secrets, IEnumerable<ExchangeAccount> accounts)
    {
        foreach (var account in accounts)
        {
            await secrets.StoreSecretAsync(account.CredentialReference, "secret-value");
            await secrets.StoreSecretAsync(account.CredentialReference + "/key", "public-key");
        }
    }

    private sealed class FixedGateway : IExchangeBalanceGateway
    {
        public ExchangeKind Exchange => ExchangeKind.Kraken;

        public Task<ExchangeBalanceSnapshot> ReadBalancesAsync(
            ExchangeCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExchangeBalanceSnapshot(DateTimeOffset.UtcNow,
                [new ExchangeBalance("BTC", 1.25m, 1.25m)]));
    }

    private sealed class ThrowingGateway : IExchangeBalanceGateway
    {
        public ExchangeKind Exchange => ExchangeKind.Kraken;

        public Task<ExchangeBalanceSnapshot> ReadBalancesAsync(
            ExchangeCredential credential, CancellationToken cancellationToken = default) =>
            throw new ExchangeBalanceReadException("Failed reading balance.");
    }

    private sealed class FakeAccounts : IExchangeAccountRepository
    {
        public FakeAccounts(params ExchangeAccount[] accounts) => All = accounts.ToList();

        public List<ExchangeAccount> All { get; }

        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(All.SingleOrDefault(account => account.Id == id));

        public Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(
            Guid userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ExchangeAccount>>(
                All.Where(account => account.UserId == userId).ToArray());

        public Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
