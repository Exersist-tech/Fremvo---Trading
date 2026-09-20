using Trading.Domain.Market;
using System.Collections.ObjectModel;

namespace Trading.Workers.MarketData;

public sealed class MarketDataStreamingOptions
{
    public const string SectionName = "MarketDataStreaming";

    public bool Enabled { get; init; }

    public Collection<string> Symbols { get; init; } = [];

    public Collection<CandleInterval> Intervals { get; init; } = [];

    public bool DeriveTenMinuteCandles { get; init; }

    public int MaximumReconnectDelaySeconds { get; init; } = 30;
}
