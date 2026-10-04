namespace Trading.Infrastructure.Data;

public sealed class AdministratorMfaRedemption
{
    public Guid UserId { get; set; }
    public long TimeStep { get; set; }
    public DateTimeOffset UsedAtUtc { get; set; }
}
