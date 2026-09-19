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
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public ExchangeKind ExchangeKind { get; }

    public string DisplayName { get; }

    public string CredentialReference { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset? LastValidatedAtUtc { get; private set; }

    public ExchangeAccountStatus Status { get; private set; }

    public bool CanTrade => Status == ExchangeAccountStatus.Connected && LastValidatedAtUtc.HasValue;

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
    }

    public void MarkSuspended(DateTimeOffset nowUtc)
    {
        Status = ExchangeAccountStatus.Suspended;
        LastValidatedAtUtc = nowUtc;
    }
}
