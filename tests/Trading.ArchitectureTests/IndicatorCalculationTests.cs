using Trading.Domain.Market;
using Trading.Indicators;
using Trading.MarketData;

namespace Trading.ArchitectureTests;

public sealed class IndicatorCalculationTests
{
    [Fact]
    public void SimpleMovingAverageUsesTrailingClosedCandles()
    {
        var result = new SimpleMovingAverageCalculator(3).Calculate(Candles(10m, 12m, 14m, 16m));

        Assert.True(result.IsReady);
        Assert.Equal(14m, result.Value);
    }

    [Fact]
    public void ExponentialMovingAverageUsesSmaSeed()
    {
        var result = new ExponentialMovingAverageCalculator(3).Calculate(Candles(10m, 12m, 14m, 16m));

        Assert.True(result.IsReady);
        Assert.Equal(14m, result.Value);
    }

    [Fact]
    public void RelativeStrengthIndexUsesWilderSmoothing()
    {
        var result = new RelativeStrengthIndexCalculator(3).Calculate(Candles(1m, 2m, 3m, 2m, 4m));

        Assert.True(result.IsReady);
        Assert.Equal(83.33333333333333333333333333m, result.Value!.Value, 24);
    }

    [Fact]
    public void MacdUsesSmaSeededEmaForLineAndSignal()
    {
        var result = new MovingAverageConvergenceDivergenceCalculator(2, 3, 2)
            .Calculate(Candles(1m, 2m, 3m, 4m, 10m));

        Assert.True(result.IsReady);
        Assert.Equal(1.33333333333333333333333333m, result.Value!.Value.Line, 24);
        Assert.Equal(1.05555555555555555555555555m, result.Value.Value.Signal, 24);
        Assert.Equal(0.27777777777777777777777778m, result.Value.Value.Histogram, 24);
    }

    [Fact]
    public void BollingerBandsUseDecimalPopulationStandardDeviation()
    {
        var result = new BollingerBandsCalculator(3).Calculate(Candles(1m, 2m, 3m));

        Assert.True(result.IsReady);
        Assert.Equal(2m, result.Value!.Value.Middle);
        Assert.Equal(0.81649658092772603273242802m, result.Value.Value.StandardDeviation, 24);
        Assert.Equal(3.63299316185545206546485604m, result.Value.Value.Upper, 24);
        Assert.Equal(0.36700683814454793453514396m, result.Value.Value.Lower, 24);
    }

    [Fact]
    public void AverageTrueRangeUsesPriorCloseAndWilderSmoothing()
    {
        var candles = new[]
        {
            Candle(0, 9m, 10m, 8m),
            Candle(1, 11m, 12m, 9m),
            Candle(2, 12m, 13m, 10m),
            Candle(3, 14m, 15m, 11m),
        };

        var result = new AverageTrueRangeCalculator(3).Calculate(candles);

        Assert.True(result.IsReady);
        Assert.Equal(28m / 9m, result.Value!.Value, 24);
    }

    [Theory]
    [InlineData("sma")]
    [InlineData("ema")]
    [InlineData("rsi")]
    [InlineData("macd")]
    [InlineData("bollinger")]
    [InlineData("atr")]
    public void InsufficientHistoryIsExplicit(string indicator)
    {
        var candles = Candles(10m, 11m);
        var result = indicator switch
        {
            "sma" => new SimpleMovingAverageCalculator(3).Calculate(candles).IsReady,
            "ema" => new ExponentialMovingAverageCalculator(3).Calculate(candles).IsReady,
            "rsi" => new RelativeStrengthIndexCalculator(2).Calculate(candles).IsReady,
            "macd" => new MovingAverageConvergenceDivergenceCalculator(2, 3, 2).Calculate(candles).IsReady,
            "bollinger" => new BollingerBandsCalculator(3).Calculate(candles).IsReady,
            "atr" => new AverageTrueRangeCalculator(3).Calculate(candles).IsReady,
            _ => throw new InvalidOperationException(),
        };

        Assert.False(result);
    }

    [Fact]
    public void FormingCandleCannotBeUsedAsLookAheadData()
    {
        var candles = Candles(10m, 12m, 14m).ToList();
        candles.Add(Candle(3, 1000m, isClosed: false));

        Assert.Throws<ArgumentException>(() => new SimpleMovingAverageCalculator(3).Calculate(candles));
    }

    [Fact]
    public void CandleUnsafeForClosedSignalIsRejected()
    {
        var candles = Candles(10m, 12m, 14m).ToArray();
        candles[1] = Candle(1, 12m, qualityFlags: new[] { DataQualityIssue.Stale });

        Assert.Throws<ArgumentException>(() => new ExponentialMovingAverageCalculator(3).Calculate(candles));
    }

    [Fact]
    public void MismatchedOrNonAscendingCandleSeriesIsRejected()
    {
        var mismatch = Candles(10m, 12m, 14m).ToArray();
        mismatch[2] = new Candle("ETH/USD", CandleInterval.OneHour, mismatch[2].OpenTimeUtc, mismatch[2].CloseTimeUtc, 14m, 14m, 14m, 14m, 1m, true, false);
        var nonAscending = new[] { Candle(1, 10m), Candle(0, 11m), Candle(2, 12m) };

        Assert.Throws<ArgumentException>(() => new SimpleMovingAverageCalculator(3).Calculate(mismatch));
        Assert.Throws<ArgumentException>(() => new SimpleMovingAverageCalculator(3).Calculate(nonAscending));
    }

    [Fact]
    public void IndicatorsReferenceNoExchangeOrInfrastructureAssemblies()
    {
        var references = typeof(SimpleMovingAverageCalculator).Assembly.GetReferencedAssemblies().Select(reference => reference.Name);

        Assert.DoesNotContain(references, name =>
            name is not null
            && (name.Contains("Kraken", StringComparison.Ordinal)
                || name.Contains("Azure", StringComparison.Ordinal)
                || name.Contains("EntityFramework", StringComparison.Ordinal)
                || name.Contains("AspNetCore", StringComparison.Ordinal)
                || name.Contains("Web", StringComparison.Ordinal)));
    }

    private static Candle[] Candles(params decimal[] closes) =>
        closes.Select((close, index) => Candle(index, close)).ToArray();

    private static Candle Candle(int index, decimal close, decimal? high = null, decimal? low = null, bool isClosed = true, IReadOnlyCollection<DataQualityIssue>? qualityFlags = null)
    {
        var open = DateTimeOffset.UnixEpoch.AddHours(index);
        return new Candle(
            "BTC/USD",
            CandleInterval.OneHour,
            open,
            open.AddHours(1),
            close,
            high ?? close,
            low ?? close,
            close,
            1m,
            isClosed,
            false,
            qualityFlags);
    }
}
