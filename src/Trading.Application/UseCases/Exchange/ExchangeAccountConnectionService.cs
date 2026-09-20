using Trading.Exchanges.Abstractions;
using Trading.Infrastructure.Secrets;

namespace Trading.Application.UseCases.Exchange;

/// <summary>
/// The outcome of a connection attempt. Connection failures here are expected
/// business outcomes rather than faults, so they are returned explicitly
/// instead of thrown.
/// </summary>
public enum ExchangeConnectionOutcome
{
    Connected = 0,

    /// <summary>The credential cannot read account state, so it is useless.</summary>
    MissingReadPermission = 1,

    /// <summary>The credential cannot place orders.</summary>
    MissingTradePermission = 2,

    /// <summary>
    /// The credential can withdraw funds. This is always rejected. The platform
    /// has no withdrawal code anywhere, and holding such a key would give it a
    /// capability it must never have.
    /// </summary>
    WithdrawalPermissionPresent = 3,

    /// <summary>The exchange could not be reached to check the credential.</summary>
    ProbeFailed = 4,

    /// <summary>
    /// The supplied values are not a usable credential, for example a private
    /// key that is not in the format the exchange issues. Nothing was stored.
    /// </summary>
    CredentialNotUsable = 5
}

/// <summary>
/// The result of connecting an exchange account. It deliberately carries no
/// credential material.
/// </summary>
public sealed record ExchangeConnectionResult(
    ExchangeConnectionOutcome Outcome,
    ExchangeAccount? Account,
    string? FailureReason)
{
    public bool IsSuccess => Outcome == ExchangeConnectionOutcome.Connected;

    public static ExchangeConnectionResult Success(ExchangeAccount account) =>
        new(ExchangeConnectionOutcome.Connected, account, null);

    public static ExchangeConnectionResult Failure(ExchangeConnectionOutcome outcome, string reason) =>
        new(outcome, null, reason);
}

public interface IExchangeAccountConnectionService
{
    Task<ExchangeConnectionResult> ConnectAsync(
        Guid userId,
        ExchangeKind exchangeKind,
        string displayName,
        ExchangeCredential credential,
        CancellationToken cancellationToken = default);

    Task<bool> DisconnectAsync(Guid userId, Guid accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ExchangeAccount>> ListAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Connects a user's exchange account, storing the credential in the secret
/// store and keeping only a reference in the database.
/// </summary>
/// <remarks>
/// <para>
/// The credential is written to the secret store and then dropped. Nothing in
/// this class returns it, logs it, or persists it to SQL. The database row
/// holds a secret <em>name</em> only, so a database compromise alone does not
/// yield a tradable key.
/// </para>
/// <para>
/// Every method takes the owning user id and refuses to act on an account that
/// belongs to someone else, so one user can never address another user's
/// connection by guessing an identifier.
/// </para>
/// </remarks>
public sealed class ExchangeAccountConnectionService : IExchangeAccountConnectionService
{
    private readonly IExchangeAccountRepository _accounts;
    private readonly ISecretStore _secrets;
    private readonly Dictionary<ExchangeKind, IExchangePermissionProbe> _probes;
    private readonly IExchangeAccountService _accountService;
    private readonly TimeProvider _timeProvider;

    public ExchangeAccountConnectionService(
        IExchangeAccountRepository accounts,
        ISecretStore secrets,
        IEnumerable<IExchangePermissionProbe> probes,
        IExchangeAccountService accountService,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(probes);

        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _probes = probes.ToDictionary(probe => probe.Exchange);
    }

    /// <summary>
    /// Builds the secret name for an account. The name includes the owning user
    /// so that two users' credentials can never collide, and contains no
    /// credential material itself.
    /// </summary>
    public static string BuildSecretName(Guid userId, Guid accountId) =>
        $"exchange-credential/{userId:D}/{accountId:D}";

    public async Task<ExchangeConnectionResult> ConnectAsync(
        Guid userId,
        ExchangeKind exchangeKind,
        string displayName,
        ExchangeCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (!_probes.TryGetValue(exchangeKind, out var probe))
        {
            return ExchangeConnectionResult.Failure(
                ExchangeConnectionOutcome.ProbeFailed,
                $"No connector is available for {exchangeKind}.");
        }

        ApiPermissionSnapshot permissions;
        try
        {
            permissions = await probe.ProbeAsync(credential, cancellationToken).ConfigureAwait(false);
        }
        catch (ExchangeCredentialFormatException exception)
        {
            // The values supplied are not a credential. This is an expected
            // user input failure, so it is returned rather than thrown. The
            // connector guarantees the message carries no credential material.
            return ExchangeConnectionResult.Failure(
                ExchangeConnectionOutcome.CredentialNotUsable,
                exception.Message);
        }
        catch (ExchangePermissionProbeException exception)
        {
            // The message is written by the connector and is required to be
            // free of credential material.
            return ExchangeConnectionResult.Failure(
                ExchangeConnectionOutcome.ProbeFailed,
                exception.Message);
        }

        // Withdrawal capability is checked first and is never recoverable. A key
        // that can move funds is refused outright rather than stored and
        // narrowed later.
        if (permissions.CanWithdraw)
        {
            return ExchangeConnectionResult.Failure(
                ExchangeConnectionOutcome.WithdrawalPermissionPresent,
                "This API key can withdraw funds. Create a key without withdrawal permission. " +
                "Fremvo Trading never withdraws or transfers funds and will not hold a key that can.");
        }

        if (!permissions.CanRead)
        {
            return ExchangeConnectionResult.Failure(
                ExchangeConnectionOutcome.MissingReadPermission,
                "This API key cannot read account data, which the platform needs before it can act safely.");
        }

        if (!permissions.CanTrade)
        {
            return ExchangeConnectionResult.Failure(
                ExchangeConnectionOutcome.MissingTradePermission,
                "This API key cannot place orders.");
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var accountId = Guid.NewGuid();
        var secretName = BuildSecretName(userId, accountId);

        // The secret is stored before the row that references it, so a failure
        // here cannot leave a database row pointing at a secret that was never
        // written.
        await _secrets.StoreSecretAsync(
            secretName,
            credential.ApiSecret,
            version: null,
            cancellationToken).ConfigureAwait(false);

        await _secrets.StoreSecretAsync(
            secretName + "/key",
            credential.ApiKey,
            version: null,
            cancellationToken).ConfigureAwait(false);

        var account = new ExchangeAccount(
            accountId,
            userId,
            exchangeKind,
            displayName,
            secretName,
            nowUtc);

        _accountService.ValidateConnection(account, permissions, nowUtc);

        await _accounts.AddAsync(account, cancellationToken).ConfigureAwait(false);

        return ExchangeConnectionResult.Success(account);
    }

    public async Task<bool> DisconnectAsync(
        Guid userId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetByIdAsync(accountId, cancellationToken).ConfigureAwait(false);

        // An account owned by another user is reported as absent rather than
        // forbidden, so this endpoint cannot be used to discover which account
        // identifiers exist.
        if (account is null || account.UserId != userId)
        {
            return false;
        }

        account.MarkDisconnected();
        await _accounts.UpdateAsync(account, cancellationToken).ConfigureAwait(false);

        await _secrets.RemoveSecretAsync(account.CredentialReference, cancellationToken).ConfigureAwait(false);
        await _secrets.RemoveSecretAsync(account.CredentialReference + "/key", cancellationToken).ConfigureAwait(false);

        return true;
    }

    public Task<IReadOnlyCollection<ExchangeAccount>> ListAsync(
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _accounts.ListForUserAsync(userId, cancellationToken);
}
