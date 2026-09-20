using System.Globalization;
using Trading.Application.UseCases.Exchange;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Kraken;
using Trading.Infrastructure.Secrets;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers the ways a user's attempt to connect a Kraken key can fail, and the
/// nonce sequencing the probe depends on.
/// </summary>
/// <remarks>
/// These exist because a malformed private key previously escaped as an
/// unhandled exception, which reached the browser as a server error rather
/// than as an answer, and because a nonce collision was reported as a bad key.
/// </remarks>
public sealed class KrakenCredentialFailureTests
{
    private const string ApiKey = "kraken-api-key-1234567890";
    private const string ValidBase64Secret = "c29tZS1sb25nLXNlY3JldC12YWx1ZQ==";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SigningARequestWithANonBase64SecretIsReportedAsAFormatProblem()
    {
        // Kraken issues the private key as base64. A pasted value that is not
        // base64 cannot be signed, and must be reported as something the user
        // can correct.
        var exception = Assert.Throws<FormatException>(
            () => KrakenRequestSigner.Sign("/0/private/Balance", "1", "nonce=1", "this is not base64!"));

        Assert.Contains("base64", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SigningARequestNeverEchoesTheSecretInTheFailureMessage()
    {
        const string secret = "totally-not-base64-but-secret";

        var exception = Assert.Throws<FormatException>(
            () => KrakenRequestSigner.Sign("/0/private/Balance", "1", "nonce=1", secret));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectReportsAnUnusableCredentialWithoutStoringAnything()
    {
        var harness = new Harness(new FormatFailingProbe());

        var result = await harness.Service.ConnectAsync(
            harness.UserId,
            ExchangeKind.Kraken,
            "Kraken main",
            new ExchangeCredential(ApiKey, ValidBase64Secret));

        // Reported as an outcome rather than thrown: the user supplied
        // something wrong, which is expected, not a fault.
        Assert.Equal(ExchangeConnectionOutcome.CredentialNotUsable, result.Outcome);
        Assert.False(result.IsSuccess);

        // A rejected credential must leave nothing behind.
        Assert.Empty(harness.Accounts.All);
        Assert.Empty(harness.Secrets.Names);
    }

    [Fact]
    public async Task AnUnusableCredentialIsDistinguishedFromAnUnreachableExchange()
    {
        var unusable = new Harness(new FormatFailingProbe());
        var unreachable = new Harness(new UnreachableProbe());

        var unusableResult = await unusable.Service.ConnectAsync(
            unusable.UserId, ExchangeKind.Kraken, "A", new ExchangeCredential(ApiKey, ValidBase64Secret));
        var unreachableResult = await unreachable.Service.ConnectAsync(
            unreachable.UserId, ExchangeKind.Kraken, "B", new ExchangeCredential(ApiKey, ValidBase64Secret));

        // "Your key is wrong" and "we could not ask Kraken" require different
        // actions from the user, so they must not collapse into one outcome.
        Assert.NotEqual(unusableResult.Outcome, unreachableResult.Outcome);
        Assert.Equal(ExchangeConnectionOutcome.ProbeFailed, unreachableResult.Outcome);
    }

    [Fact]
    public void AnInvalidNonceIsNotReportedAsABadKey()
    {
        var exception = Assert.Throws<ExchangePermissionProbeException>(
            () => KrakenPermissionProbe.InterpretResponse("{\"error\":[\"EAPI:Invalid nonce\"],\"result\":{}}"));

        // Telling the user to re-copy a key that is correct wastes their time
        // and hides the real cause.
        Assert.Contains("nonce", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("copied in full", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnInvalidKeyIsStillReportedAsABadKey()
    {
        var exception = Assert.Throws<ExchangePermissionProbeException>(
            () => KrakenPermissionProbe.InterpretResponse("{\"error\":[\"EAPI:Invalid key\"],\"result\":{}}"));

        Assert.Contains("key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermissionDeniedStillReadsAsPermissionAbsent()
    {
        // This must stay an answer rather than an error: it is how the probe
        // establishes that a key lacks a capability.
        Assert.False(
            KrakenPermissionProbe.InterpretResponse(
                "{\"error\":[\"EGeneral:Permission denied\"],\"result\":{}}"));
    }

    [Fact]
    public void NoncesAlwaysIncreaseEvenWhenTheClockDoesNotMove()
    {
        // The probe makes three private calls in a row. A clock-derived nonce
        // can repeat inside one tick, and Kraken rejects a repeated nonce.
        var source = new KrakenNonceSource(new FixedTimeProvider(Now));

        var nonces = Enumerable.Range(0, 50).Select(_ => long.Parse(source.NextNonce(), CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(nonces.Count, nonces.Distinct().Count());
        Assert.Equal(nonces.OrderBy(value => value).ToList(), nonces);
    }

    [Fact]
    public void NoncesNeverGoBackwardsWhenTheClockDoes()
    {
        var clock = new MovableTimeProvider(Now);
        var source = new KrakenNonceSource(clock);

        var first = long.Parse(source.NextNonce(), CultureInfo.InvariantCulture);

        // A clock correction must not produce a nonce Kraken will refuse for
        // the rest of the key's life.
        clock.Now = Now.AddMinutes(-5);
        var second = long.Parse(source.NextNonce(), CultureInfo.InvariantCulture);

        Assert.True(second > first);
    }

    [Fact]
    public void NoncesAreUniqueUnderConcurrentUse()
    {
        var source = new KrakenNonceSource(new FixedTimeProvider(Now));
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 500, _ => results.Add(source.NextNonce()));

        Assert.Equal(500, results.Distinct().Count());
    }

    private sealed class Harness
    {
        public Harness(IExchangePermissionProbe probe)
        {
            Accounts = new FakeAccounts();
            Secrets = new FakeSecrets();

            Service = new ExchangeAccountConnectionService(
                Accounts,
                Secrets,
                new[] { probe },
                new ExchangeAccountService(),
                new FixedTimeProvider(Now));
        }

        public Guid UserId { get; } = Guid.NewGuid();

        public FakeAccounts Accounts { get; }

        public FakeSecrets Secrets { get; }

        public ExchangeAccountConnectionService Service { get; }
    }

    private sealed class FormatFailingProbe : IExchangePermissionProbe
    {
        public ExchangeKind Exchange => ExchangeKind.Kraken;

        public Task<ApiPermissionSnapshot> ProbeAsync(
            ExchangeCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new ExchangeCredentialFormatException(
                "The private key is not in the format Kraken issues.");
    }

    private sealed class UnreachableProbe : IExchangePermissionProbe
    {
        public ExchangeKind Exchange => ExchangeKind.Kraken;

        public Task<ApiPermissionSnapshot> ProbeAsync(
            ExchangeCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new ExchangePermissionProbeException("Kraken could not be reached to check this API key.");
    }

    private sealed class FakeAccounts : IExchangeAccountRepository
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

    private sealed class FakeSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Names => _values.Keys;

        public ValueTask StoreSecretAsync(
            string secretName,
            string secretValue,
            string? version = null,
            CancellationToken cancellationToken = default)
        {
            _values[secretName] = secretValue;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string> GetSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values[secretName]);

        public ValueTask<string?> GetSecretVersionAsync(
            string secretName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("v1");

        public ValueTask RemoveSecretAsync(string secretName, CancellationToken cancellationToken = default)
        {
            _values.Remove(secretName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class MovableTimeProvider : TimeProvider
    {
        public MovableTimeProvider(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
