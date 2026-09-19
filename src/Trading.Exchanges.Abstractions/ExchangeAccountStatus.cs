namespace Trading.Exchanges.Abstractions;

public enum ExchangeAccountStatus
{
    Disconnected = 0,
    PendingValidation = 1,
    Connected = 2,
    Suspended = 3
}
