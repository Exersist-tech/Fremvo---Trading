using Trading.Domain.Market;

namespace Trading.Infrastructure.Data.Scanner;

public sealed class PersistedScanRequest
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public string Name { get; set; } = null!;

    public string Symbols { get; set; } = null!;

    public CandleInterval Interval { get; set; }

    public string Criteria { get; set; } = null!;

    public int ResultLimit { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}
