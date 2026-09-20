using Trading.Domain.Market;

namespace Trading.Infrastructure.Data.MarketData;

public sealed class PersistedCandle
{
    public string Symbol { get; set; } = null!;

    public CandleInterval Interval { get; set; }

    public DateTimeOffset OpenTimeUtc { get; set; }

    public DateTimeOffset CloseTimeUtc { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public decimal Volume { get; set; }

    public bool IsClosed { get; set; }

    public bool IsDerived { get; set; }

    public string QualityFlags { get; set; } = null!;
}
