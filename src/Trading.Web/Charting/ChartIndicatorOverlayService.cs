using Trading.Indicators;
using Trading.MarketData;

namespace Trading.Web.Charting;

public sealed class ChartIndicatorOverlayService
{
    public IReadOnlyList<ChartIndicatorOverlay> Calculate(
        IReadOnlyList<Candle> candles,
        IReadOnlyCollection<string> requestedIndicators,
        int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(requestedIndicators);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);

        var closedCandles = candles.Where(candle => candle.IsClosed).ToArray();
        return requestedIndicators.Select(indicator =>
            CalculateIndicator(indicator, closedCandles, period)).ToArray();
    }

    private static ChartIndicatorOverlay CalculateIndicator(
        string indicator,
        Candle[] closedCandles,
        int period)
    {
        if (closedCandles.Any(candle => !candle.CanBeUsedForClosedCandleSignal))
        {
            return ChartIndicatorOverlay.Unsafe(
                indicator,
                period,
                closedCandles.Length,
                "Closed candle history contains quality issues and cannot be used for an overlay.");
        }

        try
        {
            return indicator switch
            {
                "sma" => CalculateSeries(
                    "sma",
                    closedCandles,
                    period,
                    candles => new SimpleMovingAverageCalculator(period).Calculate(candles)),
                "ema" => CalculateSeries(
                    "ema",
                    closedCandles,
                    period,
                    candles => new ExponentialMovingAverageCalculator(period).Calculate(candles)),
                _ => throw new ArgumentOutOfRangeException(nameof(indicator)),
            };
        }
        catch (ArgumentException exception)
        {
            return ChartIndicatorOverlay.Unsafe(
                indicator,
                period,
                closedCandles.Length,
                $"Closed candle history is unsafe: {exception.Message}");
        }
    }

    private static ChartIndicatorOverlay CalculateSeries(
        string name,
        Candle[] candles,
        int period,
        Func<IReadOnlyList<Candle>, IndicatorResult<decimal>> calculate)
    {
        if (candles.Length < period)
        {
            return ChartIndicatorOverlay.InsufficientHistory(name, period, candles.Length);
        }

        // Evaluate each prefix server-side so every point only uses candles that
        // had closed at that point; the browser only maps returned values to pixels.
        var points = new List<ChartIndicatorPoint>(candles.Length - period + 1);
        for (var count = period; count <= candles.Length; count++)
        {
            var result = calculate(candles.Take(count).ToArray());
            points.Add(new ChartIndicatorPoint(candles[count - 1].OpenTimeUtc, result.Value!.Value));
        }

        return ChartIndicatorOverlay.Ready(name, period, candles.Length, points);
    }
}

public sealed record ChartIndicatorPoint(DateTimeOffset OpenTimeUtc, decimal Value);

public sealed record ChartIndicatorOverlay(
    string Name,
    int Period,
    string Status,
    int AvailableClosedCandles,
    string? Message,
    IReadOnlyList<ChartIndicatorPoint> Points)
{
    public static ChartIndicatorOverlay Ready(
        string name,
        int period,
        int availableClosedCandles,
        IReadOnlyList<ChartIndicatorPoint> points) =>
        new(name, period, "Ready", availableClosedCandles, null, points);

    public static ChartIndicatorOverlay InsufficientHistory(string name, int period, int availableClosedCandles) =>
        new(
            name,
            period,
            "InsufficientHistory",
            availableClosedCandles,
            $"Requires {period} safe closed candles; only {availableClosedCandles} are available.",
            Array.Empty<ChartIndicatorPoint>());

    public static ChartIndicatorOverlay Unsafe(
        string name,
        int period,
        int availableClosedCandles,
        string message) =>
        new(name, period, "UnsafeHistory", availableClosedCandles, message, Array.Empty<ChartIndicatorPoint>());
}
