namespace Trading.Exchanges.Abstractions;

public sealed class ExchangeAccount
{
    public ExchangeAccount(
        Guid id,
        Guid userId,
        ExchangeKind exchangeKind,
        string displayName,
        string credentialReference,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Exchange account id is required.", nameof(id));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(credentialReference))
        {
            throw new ArgumentException("Credential reference is required.", nameof(credentialReference));
        }

        Id = id;
        UserId = userId;
        ExchangeKind = exchangeKind;
        DisplayName = displayName.Trim();
        CredentialReference = credentialReference.Trim();
        CreatedAtUtc = createdAtUtc;
        Status = ExchangeAccountStatus.Disconnected;
        Stage = TradingStage.Paper;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public ExchangeKind ExchangeKind { get; }

    public string DisplayName { get; }

    public string CredentialReference { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? LastValidatedAtUtc { get; private set; }

    public ExchangeAccountStatus Status { get; private set; }

    /// <summary>
    /// How far this account has been cleared to trade. Every account starts in
    /// <see cref="TradingStage.Paper"/>.
    /// </summary>
    public TradingStage Stage { get; private set; }

    public bool CanTrade => Status == ExchangeAccountStatus.Connected && LastValidatedAtUtc.HasValue;

    /// <summary>
    /// Whether this account may send an order to the real exchange. A paper
    /// account never may, no matter how healthy its connection is.
    /// </summary>
    public bool CanReachExchange => CanTrade && Stage != TradingStage.Paper;

    /// <summary>
    /// Moves the account one stage forward. Stages cannot be skipped, and an
    /// account that is not currently connected and validated cannot advance at
    /// all, so a stale or broken connection can never be promoted.
    /// </summary>
    /// <param name="target">The stage to move to. Must be exactly one step ahead.</param>
    /// <param name="nowUtc">The promotion time, recorded for audit.</param>
    public void Promote(TradingStage target, DateTimeOffset nowUtc)
    {
        if (!CanTrade)
        {
            throw new InvalidOperationException(
                "An exchange account must be connected and validated before it can be promoted.");
        }

        if (target != Stage + 1)
        {
            throw new InvalidOperationException(
                $"An exchange account cannot move from {Stage} to {target}. Stages advance one step at a time.");
        }

        Stage = target;
        StageChangedAtUtc = nowUtc;
    }

    /// <summary>
    /// Returns the account to simulated money. This is always permitted: a
    /// safety action must never be blocked by the state it is correcting.
    /// </summary>
    public void ReturnToPaper(DateTimeOffset nowUtc)
    {
        Stage = TradingStage.Paper;
        StageChangedAtUtc = nowUtc;
    }

    public DateTimeOffset? StageChangedAtUtc { get; private set; }

    public void MarkPendingValidation(DateTimeOffset nowUtc)
    {
        Status = ExchangeAccountStatus.PendingValidation;
        LastValidatedAtUtc = nowUtc;
    }

    public void MarkConnected(DateTimeOffset nowUtc)
    {
        Status = ExchangeAccountStatus.Connected;
        LastValidatedAtUtc = nowUtc;
    }

    public void MarkDisconnected()
    {
        Status = ExchangeAccountStatus.Disconnected;
        LastValidatedAtUtc = null;

        // A disconnected account drops back to simulated money. Re-connecting a
        // key must not silently restore a previously granted live clearance.
        Stage = TradingStage.Paper;
    }

    public void MarkSuspended(DateTimeOffset nowUtc)
    {
        Status = ExchangeAccountStatus.Suspended;
        LastValidatedAtUtc = nowUtc;
        Stage = TradingStage.Paper;
    }
}
