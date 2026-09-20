using System.Linq.Expressions;
using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class StrategyContractTests
{
    private static readonly StrategyTemplateId s_templateId = new("approved-test-v1");
    private static readonly DateTimeOffset s_start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] s_nonExecutablePropertyNames =
        ["Order", "Quantity", "Price", "Account", "Execution", "Exchange"];

    [Fact]
    public void ParameterSetRejectsUnknownOutOfRangeAndTypeInvalidValues()
    {
        var definitions = new[]
        {
            new StrategyParameterDefinition(
                "period",
                2m,
                20m,
                5m,
                "Closed-candle lookback.",
                valueType: StrategyParameterValueType.WholeNumber)
        };

        Assert.Throws<ArgumentException>(() => new StrategyParameterSet(
            definitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["unknown"] = StrategyParameterValue.FromNumeric(1m)
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrategyParameterSet(
            definitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["period"] = StrategyParameterValue.WholeNumber(21m)
            }));
        Assert.Throws<ArgumentException>(() => new StrategyParameterSet(
            definitions,
            new Dictionary<string, StrategyParameterValue>
            {
                ["period"] = StrategyParameterValue.FromNumeric(5m)
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrategyParameterValue.WholeNumber(5.5m));
    }

    [Fact]
    public void EvaluationInputRejectsUnclosedUnsafeAndNonChronologicalCandles()
    {
        var valid = CandleAt(0);
        var unclosed = new Candle(
            valid.Symbol, valid.Interval, valid.OpenTimeUtc.AddMinutes(1), valid.CloseTimeUtc.AddMinutes(1),
            valid.Open, valid.High, valid.Low, valid.Close, valid.Volume, false, false);
        var unsafeCandle = new Candle(
            valid.Symbol, valid.Interval, valid.OpenTimeUtc.AddMinutes(1), valid.CloseTimeUtc.AddMinutes(1),
            valid.Open, valid.High, valid.Low, valid.Close, valid.Volume, true, false,
            new[] { DataQualityIssue.Stale });

        Assert.Throws<ArgumentException>(() => Input(valid, unclosed));
        Assert.Throws<ArgumentException>(() => Input(valid, unsafeCandle));
        Assert.Throws<ArgumentException>(() => Input(CandleAt(1), valid));
    }

    [Fact]
    public void EvaluationInputRejectsInvalidOrFutureState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyState(s_templateId, -1, s_start));

        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(
            s_templateId,
            new StrategyParameterSet(Array.Empty<StrategyParameterDefinition>()),
            new StrategyState(s_templateId, 0, CandleAt(1).CloseTimeUtc),
            new[] { CandleAt(0) }));

        Assert.Throws<ArgumentException>(() => new StrategyEvaluationInput(
            s_templateId,
            new StrategyParameterSet(Array.Empty<StrategyParameterDefinition>()),
            new StrategyState(new StrategyTemplateId("different-template"), 0, s_start),
            new[] { CandleAt(0) }));
    }

    [Fact]
    public void EvaluationIsDeterministicAndNeverExposesFutureCandles()
    {
        var strategy = new CountingStrategy();
        var candles = new[] { CandleAt(0), CandleAt(1), CandleAt(2) };
        var results = new List<StrategyAnalysisProposal>();

        for (var index = 0; index < candles.Length; index++)
        {
            var input = Input(candles.Take(index + 1).ToArray());
            var first = strategy.Evaluate(input);
            var second = strategy.Evaluate(input);

            Assert.Equal(first.Direction, second.Direction);
            Assert.Equal(first.Confidence, second.Confidence);
            Assert.Equal(candles[index].CloseTimeUtc, input.AsOfUtc);
            Assert.Equal(index + 1, input.ClosedCandles.Count);
            Assert.DoesNotContain(input.ClosedCandles, candle => candle.CloseTimeUtc > input.AsOfUtc);
            results.Add(first);
        }

        Assert.Equal(new[] { 1m / 10m, 2m / 10m, 3m / 10m }, results.Select(result => result.Confidence));
    }

    [Fact]
    public void TimeframeRolesAreImmutableSeparateAndAlignedToExecution()
    {
        var timeframes = new StrategyTimeframeConfiguration(
            CandleInterval.OneHour,
            CandleInterval.FifteenMinutes,
            CandleInterval.FiveMinutes);
        var regime = Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, 0, 60);
        var signal = Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, 45, 60);
        var execution = Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, 55, 60);

        var input = new StrategyEvaluationInput(
            s_templateId,
            new StrategyParameterSet(Array.Empty<StrategyParameterDefinition>()),
            new StrategyState(s_templateId, 0, s_start),
            timeframes,
            [regime, signal, execution]);

        Assert.Equal(timeframes, input.Timeframes);
        Assert.Same(regime, input.Regime);
        Assert.Same(signal, input.Signal);
        Assert.Same(execution, input.Execution);
        Assert.Equal(s_start.AddMinutes(60), input.AsOfUtc);
        Assert.All(new[] { input.Regime, input.Signal, input.Execution }, series =>
            Assert.DoesNotContain(series.ClosedCandles, candle => candle.CloseTimeUtc > input.AsOfUtc));
        Assert.Null(typeof(StrategyTimeframeConfiguration).GetProperty(nameof(StrategyTimeframeConfiguration.Regime))!.SetMethod);
    }

    [Fact]
    public void TimeframeRolesRejectUnsupportedSwappedMissingDuplicateAndFutureData()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StrategyTimeframeConfiguration((CandleInterval)2, CandleInterval.OneMinute, CandleInterval.OneMinute));
        Assert.Throws<ArgumentException>(() =>
            new StrategyTimeframeConfiguration(CandleInterval.FiveMinutes, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes));
        Assert.Throws<ArgumentException>(() =>
            new StrategyTimeframeConfiguration(CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.ThirtyMinutes));

        var timeframes = new StrategyTimeframeConfiguration(
            CandleInterval.OneHour, CandleInterval.FifteenMinutes, CandleInterval.FiveMinutes);
        var regime = Series(StrategyTimeframeRole.Regime, CandleInterval.OneHour, 0, 60);
        var signal = Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, 0, 15);
        var execution = Series(StrategyTimeframeRole.Execution, CandleInterval.FiveMinutes, 0, 5, 5, 10, 10, 15);

        Assert.Throws<ArgumentException>(() => MultiInput(timeframes, [regime, signal]));
        Assert.Throws<ArgumentException>(() => MultiInput(timeframes, [regime, signal,
            Series(StrategyTimeframeRole.Signal, CandleInterval.FifteenMinutes, 15, 30)]));
        Assert.Throws<ArgumentException>(() => MultiInput(timeframes, [regime,
            Series(StrategyTimeframeRole.Signal, CandleInterval.OneHour, 0, 60), execution]));
        Assert.Throws<ArgumentException>(() => MultiInput(timeframes, [Series(
            StrategyTimeframeRole.Regime, CandleInterval.OneHour, 0, 60, 60, 120), signal, execution]));
    }

    [Fact]
    public void ContractHasNoUserCodeSurfaceAndProposalCannotBeUsedAsAnOrder()
    {
        var publicContractTypes = new[]
        {
            typeof(IStrategy),
            typeof(ApprovedStrategyCatalog),
            typeof(StrategyEvaluationInput),
            typeof(StrategyState),
            typeof(StrategyAnalysisProposal)
        };

        var publicTypes = publicContractTypes
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .OfType<MethodBase>();
        Assert.DoesNotContain(publicTypes.SelectMany(method => method.GetParameters()), parameter =>
            typeof(Delegate).IsAssignableFrom(parameter.ParameterType)
            || parameter.ParameterType == typeof(Expression)
            || (parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Expression<>)));

        var proposalProperties = typeof(StrategyAnalysisProposal).GetProperties();
        Assert.DoesNotContain(proposalProperties, property =>
            s_nonExecutablePropertyNames
                .Any(forbidden => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
        Assert.False(typeof(TradeIntent).IsAssignableFrom(typeof(StrategyAnalysisProposal)));
    }

    [Fact]
    public void StrategiesAssemblyHasNoProhibitedExchangeEfOrHttpReferences()
    {
        var references = typeof(IStrategy).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);

        Assert.DoesNotContain(references, name =>
            name.Contains("Kraken", StringComparison.Ordinal)
            || name.Contains("EntityFramework", StringComparison.Ordinal)
            || name.Contains("System.Net.Http", StringComparison.Ordinal)
            || name.Contains("Trading.Exchanges", StringComparison.Ordinal)
            || name.Contains("Trading.Indicators", StringComparison.Ordinal));
    }

    private static StrategyEvaluationInput Input(params Candle[] candles) =>
        new(
            s_templateId,
            new StrategyParameterSet(Array.Empty<StrategyParameterDefinition>()),
            new StrategyState(s_templateId, 0, s_start),
            candles);

    private static Candle CandleAt(int minute) =>
        new(
            "BTCUSD",
            CandleInterval.OneMinute,
            s_start.AddMinutes(minute),
            s_start.AddMinutes(minute + 1),
            100m,
            102m,
            99m,
            101m,
            5m,
            true,
            false);

    private static StrategyTimeframeSeries Series(
        StrategyTimeframeRole role,
        CandleInterval interval,
        params int[] minuteBoundaries)
    {
        var candles = minuteBoundaries
            .Chunk(2)
            .Select(pair => new Candle(
                "BTCUSD",
                interval,
                s_start.AddMinutes(pair[0]),
                s_start.AddMinutes(pair[1]),
                100m,
                102m,
                99m,
                101m,
                5m,
                true,
                false))
            .ToArray();
        return new StrategyTimeframeSeries(role, interval, candles);
    }

    private static StrategyEvaluationInput MultiInput(
        StrategyTimeframeConfiguration timeframes,
        IReadOnlyList<StrategyTimeframeSeries> series) =>
        new(
            s_templateId,
            new StrategyParameterSet(Array.Empty<StrategyParameterDefinition>()),
            new StrategyState(s_templateId, 0, s_start),
            timeframes,
            series);

    private sealed class CountingStrategy : IStrategy
    {
        public StrategyTemplateId TemplateId => s_templateId;

        public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions =>
            Array.Empty<StrategyParameterDefinition>();

        public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input) =>
            new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Bullish, input.ClosedCandles.Count / 10m, "Closed-candle count.");
    }
}
