using Trading.Application.UseCases.Exchange;
using Trading.Exchanges.Abstractions;
using Trading.Infrastructure.Secrets;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers connecting an exchange account: refusing a withdrawal-capable key,
/// keeping the secret out of SQL and out of responses, isolating one user's
/// accounts from another's, and the paper-by-default trading stage.
/// </summary>
public sealed class ExchangeAccountConnectionTests
{
    private const string ApiKey = "PUBLICKEY1234567890";
    private const string ApiSecret = "c2VjcmV0LXZhbHVlLXRoYXQtbXVzdC1uZXZlci1sZWFr";

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConnectRefusesAKeyThatCanWithdrawFunds()
    {
        var harness = new Harness(new ApiPermissionSnapshot(true, true, CanWithdraw: true, Now));

        var result = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        Assert.False(result.IsSuccess);
        Assert.Equal(ExchangeConnectionOutcome.WithdrawalPermissionPresent, result.Outcome);
    }

    [Fact]
    public async Task ConnectDoesNotStoreAnythingWhenAWithdrawalCapableKeyIsRefused()
    {
        var harness = new Harness(new ApiPermissionSnapshot(true, true, CanWithdraw: true, Now));

        await harness.Service.ConnectAsync(harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        // A rejected key must leave no trace: no account row and no secret.
        Assert.Empty(harness.Accounts.All);
        Assert.Empty(harness.Secrets.Names);
    }

    [Fact]
    public async Task ConnectRefusesAKeyThatCannotRead()
    {
        var harness = new Harness(new ApiPermissionSnapshot(CanRead: false, true, false, Now));

        var result = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        Assert.Equal(ExchangeConnectionOutcome.MissingReadPermission, result.Outcome);
    }

    [Fact]
    public async Task ConnectRefusesAKeyThatCannotTrade()
    {
        var harness = new Harness(new ApiPermissionSnapshot(true, CanTrade: false, false, Now));

        var result = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        Assert.Equal(ExchangeConnectionOutcome.MissingTradePermission, result.Outcome);
    }

    [Fact]
    public async Task ConnectStoresTheSecretInTheSecretStoreAndOnlyAReferenceOnTheAccount()
    {
        var harness = new Harness(Usable());

        var result = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        Assert.True(result.IsSuccess);
        var account = result.Account!;

        // The row holds a secret name, never the secret itself.
        Assert.DoesNotContain(ApiSecret, account.CredentialReference, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, account.CredentialReference, StringComparison.Ordinal);
        Assert.Equal(
            ExchangeAccountConnectionService.BuildSecretName(harness.UserId, account.Id),
            account.CredentialReference);

        Assert.Equal(ApiSecret, harness.Secrets.Values[account.CredentialReference]);
    }

    [Fact]
    public async Task ConnectNeverExposesTheSecretOnTheResult()
    {
        var harness = new Harness(Usable());

        var result = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        // Serializing the result is what an endpoint effectively does. The
        // secret must not survive that round trip.
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.DoesNotContain(ApiSecret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectLeavesTheAccountInThePaperStage()
    {
        var harness = new Harness(Usable());

        var result = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        // A freshly connected key must never be able to reach the exchange.
        Assert.Equal(TradingStage.Paper, result.Account!.Stage);
        Assert.False(result.Account.CanReachExchange);
    }

    [Fact]
    public async Task DisconnectRemovesTheStoredCredential()
    {
        var harness = new Harness(Usable());
        var connected = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        var removed = await harness.Service.DisconnectAsync(harness.UserId, connected.Account!.Id);

        Assert.True(removed);
        Assert.Empty(harness.Secrets.Names);
    }

    [Fact]
    public async Task DisconnectRefusesAnAccountOwnedByAnotherUser()
    {
        var harness = new Harness(Usable());
        var connected = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        var otherUser = Guid.NewGuid();
        var removed = await harness.Service.DisconnectAsync(otherUser, connected.Account!.Id);

        // Reported as absent rather than forbidden, so the endpoint cannot be
        // used to discover which account identifiers exist.
        Assert.False(removed);

        // And crucially, the owner's credential is untouched.
        Assert.Single(harness.Secrets.Values.Where(pair =>
            pair.Key == connected.Account.CredentialReference));
    }

    [Fact]
    public async Task ListReturnsOnlyTheRequestingUsersAccounts()
    {
        var harness = new Harness(Usable());
        await harness.Service.ConnectAsync(harness.UserId, ExchangeKind.Kraken, "Mine", Credential());

        var otherUser = Guid.NewGuid();
        await harness.Service.ConnectAsync(otherUser, ExchangeKind.Kraken, "Theirs", Credential());

        var mine = await harness.Service.ListAsync(harness.UserId);

        Assert.Single(mine);
        Assert.Equal("Mine", mine.Single().DisplayName);
    }

    [Fact]
    public async Task SecretNamesAreScopedPerUserSoTwoUsersCannotCollide()
    {
        var harness = new Harness(Usable());
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        var a = await harness.Service.ConnectAsync(userA, ExchangeKind.Kraken, "A", Credential());
        var b = await harness.Service.ConnectAsync(userB, ExchangeKind.Kraken, "B", Credential());

        Assert.NotEqual(a.Account!.CredentialReference, b.Account!.CredentialReference);
        Assert.Contains(userA.ToString("D"), a.Account.CredentialReference, StringComparison.Ordinal);
        Assert.Contains(userB.ToString("D"), b.Account.CredentialReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectReportsAProbeFailureWithoutLeakingTheCredential()
    {
        var harness = new Harness(throwOnProbe: true);

        var result = await harness.Service.ConnectAsync(
            harness.UserId, ExchangeKind.Kraken, "Kraken main", Credential());

        Assert.Equal(ExchangeConnectionOutcome.ProbeFailed, result.Outcome);
        Assert.DoesNotContain(ApiSecret, result.FailureReason!, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialToStringIsRedacted()
    {
        var credential = Credential();

        // An accidental log or interpolation must not print the secret.
        Assert.DoesNotContain(ApiSecret, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialPublicKeyHintRevealsOnlyTheLastFourCharactersOfThePublicKey()
    {
        var hint = Credential().PublicKeyHint;

        Assert.Equal("****7890", hint);
        Assert.DoesNotContain(ApiSecret, hint, StringComparison.Ordinal);
    }

    private static ExchangeCredential Credential() => new(ApiKey, ApiSecret);

    private static ApiPermissionSnapshot Usable() => new(true, true, false, Now);

    private sealed class Harness
    {
        public Harness(ApiPermissionSnapshot? permissions = null, bool throwOnProbe = false)
        {
            Accounts = new FakeAccountRepository();
            Secrets = new RecordingSecretStore();

            Service = new ExchangeAccountConnectionService(
                Accounts,
                Secrets,
                new[] { new FakeProbe(permissions ?? Usable(), throwOnProbe) },
                new ExchangeAccountService(),
                new FixedTimeProvider(Now));
        }

        public Guid UserId { get; } = Guid.NewGuid();

        public FakeAccountRepository Accounts { get; }

        public RecordingSecretStore Secrets { get; }

        public ExchangeAccountConnectionService Service { get; }
    }

    private sealed class FakeProbe : IExchangePermissionProbe
    {
        private readonly ApiPermissionSnapshot _permissions;
        private readonly bool _throws;

        public FakeProbe(ApiPermissionSnapshot permissions, bool throws)
        {
            _permissions = permissions;
            _throws = throws;
        }

        public ExchangeKind Exchange => ExchangeKind.Kraken;

        public Task<ApiPermissionSnapshot> ProbeAsync(
            ExchangeCredential credential,
            CancellationToken cancellationToken = default) =>
            _throws
                ? throw new ExchangePermissionProbeException("Kraken could not be reached to check this API key.")
                : Task.FromResult(_permissions);
    }

    private sealed class FakeAccountRepository : IExchangeAccountRepository
    {
        public List<ExchangeAccount> All { get; } = new();

        public Task<ExchangeAccount?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(All.FirstOrDefault(account => account.Id == id));

        public Task<IReadOnlyCollection<ExchangeAccount>> ListForUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<ExchangeAccount>>(
                All.Where(account => account.UserId == userId).ToList());

        public Task AddAsync(ExchangeAccount account, CancellationToken cancellationToken = default)
        {
            All.Add(account);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ExchangeAccount account, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Names => Values.Keys;

        public ValueTask StoreSecretAsync(
            string secretName,
            string secretValue,
            string? version = null,
            CancellationToken cancellationToken = default)
        {
            Values[secretName] = secretValue;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string> GetSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Values[secretName]);

        public ValueTask<string?> GetSecretVersionAsync(
            string secretName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("v1");

        public ValueTask RemoveSecretAsync(string secretName, CancellationToken cancellationToken = default)
        {
            Values.Remove(secretName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
