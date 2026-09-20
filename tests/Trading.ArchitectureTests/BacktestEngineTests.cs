using System.Reflection;
using Trading.Backtesting;
using Trading.Domain.Market;
using Trading.Domain.Strategies;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.ArchitectureTests;

public sealed class BacktestEngineTests
{
    private static readonly DateTimeOffset s_start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly int[] s_lifecycleVisibleCounts = [2, 3, 4];
    private static readonly int[] s_warmupVisibleCounts = [3, 4];
    private static readonly BacktestSimulatedAction[] s_lifecycleActions =
        [BacktestSimulatedAction.Buy, BacktestSimulatedAction.None, BacktestSimulatedAction.Sell];

    [Fact]
    public void RunsDeterministicClosedCandleLifecycleWithoutLookAhead()
    {
        var candles = Candles(10m, 10m, 12m, 15m);
        var dataset = Dataset(candles);
        var firstStrategy = new LifecycleStrategy();
        var result = BacktestEngine.Run(dataset, candles, firstStrategy, Parameters(), Configuration(dataset, 1));

        Assert.Equal(180m, result.FinalPortfolioValue);
        Assert.Equal(60m, result.NetPnL);
        Assert.Equal(2, result.TradeCount);
        Assert.Equal(0m, result.TotalFees);
        Assert.Equal(0m, result.TotalSlippage);
        Assert.Equal(s_lifecycleVisibleCounts, firstStrategy.VisibleCounts);
        Assert.Equal(s_lifecycleVisibleCounts, firstStrategy.VisibleCountsAtProposal);
        Assert.Equal(s_lifecycleActions,
            result.Events.Select(@event => @event.Action));
        Assert.All(result.EquitySnapshots, snapshot =>
        {
            Assert.True(snapshot.CashBalance >= 0m);
            Assert.True(snapshot.BaseQuantity >= 0m);
        });

        var replay = BacktestEngine.Run(dataset, candles, new LifecycleStrategy(), Parameters(), Configuration(dataset, 1));
        Assert.Equal(result.FinalPortfolioValue, replay.FinalPortfolioValue);
        Assert.Equal(result.NetPnL, replay.NetPnL);
        Assert.Equal(
            result.Events.Select(@event => (@event.ObservedAtUtc, @event.Action, @event.Quantity, @event.Price, @event.CashBalance, @event.BaseQuantity)),
            replay.Events.Select(@event => (@event.ObservedAtUtc, @event.Action, @event.Quantity, @event.Price, @event.CashBalance, @event.BaseQuantity)));
        Assert.Equal(result.EquitySnapshots, replay.EquitySnapshots);
    }

    [Fact]
    public void WarmupIsExactAndNeutralAnalysisNeverCreatesTrade()
    {
        var candles = Candles(10m, 10m, 10m, 10m);
        var dataset = Dataset(candles);
        var strategy = new NeutralStrategy();
        var result = BacktestEngine.Run(dataset, candles, strategy, Parameters(), Configuration(dataset, 2));

        Assert.Equal(s_warmupVisibleCounts, strategy.VisibleCounts);
        Assert.Equal(0, result.TradeCount);
        Assert.Equal(120m, result.FinalPortfolioValue);
        Assert.All(result.Events, @event => Assert.Equal(BacktestSimulatedAction.None, @event.Action));
    }

    [Fact]
    public void RejectsFingerprintMismatchAndInvalidCandleEvidence()
    {
        var candles = Candles(10m, 11m, 12m);
        var fingerprintMismatch = Dataset(candles, new string('A', 64));
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(
            fingerprintMismatch, candles, new NeutralStrategy(), Parameters(), Configuration(fingerprintMismatch, 0)));

        var future = Candles(10m, 11m, 12m);
        var futureDataset = Dataset(future, createdAtUtc: s_start.AddMinutes(2).AddSeconds(30));
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(
            futureDataset, future, new NeutralStrategy(), Parameters(), Configuration(futureDataset, 0)));

        var unclosed = new List<Candle>(candles)
        {
            new("BTCUSD", CandleInterval.OneMinute, s_start.AddMinutes(3), s_start.AddMinutes(4),
                12m, 12m, 12m, 12m, 1m, false, false)
        };
        var unclosedDataset = Dataset(unclosed);
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(
            unclosedDataset, unclosed, new NeutralStrategy(), Parameters(), Configuration(unclosedDataset, 0)));

        var stale = Candles(10m, 11m, 12m);
        stale[1] = new Candle("BTCUSD", CandleInterval.OneMinute, s_start.AddMinutes(1), s_start.AddMinutes(2),
            11m, 11m, 11m, 11m, 1m, true, false, new[] { DataQualityIssue.Stale });
        var staleDataset = Dataset(stale);
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(
            staleDataset, stale, new NeutralStrategy(), Parameters(), Configuration(staleDataset, 0)));
    }

    [Fact]
    public void RejectsMissingDuplicateOutOfOrderAndNonApprovedStrategies()
    {
        var missing = Candles(10m, 11m, 12m);
        missing[1] = Candle(2, 11m);
        var missingDataset = Dataset(missing);
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(missingDataset, missing, new NeutralStrategy(), Parameters(), Configuration(missingDataset, 0)));

        var duplicate = Candles(10m, 11m, 12m);
        duplicate[1] = Candle(0, 11m);
        var duplicateDataset = Dataset(duplicate);
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(duplicateDataset, duplicate, new NeutralStrategy(), Parameters(), Configuration(duplicateDataset, 0)));

        var outOfOrder = Candles(10m, 11m, 12m);
        (outOfOrder[0], outOfOrder[1]) = (outOfOrder[1], outOfOrder[0]);
        var outOfOrderDataset = Dataset(outOfOrder);
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(outOfOrderDataset, outOfOrder, new NeutralStrategy(), Parameters(), Configuration(outOfOrderDataset, 0)));

        var valid = Candles(10m, 11m, 12m);
        var validDataset = Dataset(valid);
        Assert.Throws<ArgumentException>(() => BacktestEngine.Run(validDataset, valid, new UnapprovedStrategy(), Parameters(), Configuration(validDataset, 0)));
    }

    [Fact]
    public void RejectsAccumulationAndNeverCreatesShortOrLeverage()
    {
        var candles = Candles(10m, 10m, 10m, 10m, 10m);
        var dataset = Dataset(candles);
        var result = BacktestEngine.Run(dataset, candles, new RepeatedBullishStrategy(), Parameters(), Configuration(dataset, 0));

        Assert.Equal(1, result.TradeCount);
        Assert.Equal(120m, result.FinalPortfolioValue);
        Assert.Contains(result.Events, @event => @event.Action == BacktestSimulatedAction.Rejected);
        Assert.All(result.Events, @event =>
        {
            Assert.True(@event.CashBalance >= 0m);
            Assert.True(@event.BaseQuantity >= 0m);
        });
    }

    [Fact]
    public void BacktestingAssemblyHasNoExecutionOrConnectorDependencies()
    {
        var assembly = typeof(BacktestEngine).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("Exchanges", StringComparison.Ordinal)
            || reference.Name.Contains("Risk", StringComparison.Ordinal)
            || reference.Name.Contains("Application", StringComparison.Ordinal)
            || reference.Name.Contains("Workers", StringComparison.Ordinal));
        Assert.DoesNotContain(assembly.GetTypes().SelectMany(type => type.GetMembers()).Select(member => member.Name),
            name => name.Contains("TradeIntent", StringComparison.Ordinal));
    }

    private static List<Candle> Candles(params decimal[] closes) =>
        closes.Select((close, index) => Candle(index, close)).ToList();

    private static Candle Candle(int index, decimal close) =>
        new("BTCUSD", CandleInterval.OneMinute, s_start.AddMinutes(index), s_start.AddMinutes(index + 1),
            close, close, close, close, 1m, true, false);

    private static HistoricalDataset Dataset(
        List<Candle> candles,
        string? fingerprint = null,
        DateTimeOffset? createdAtUtc = null) =>
        new("fixture", "public-archive", "BTCUSD", "1m", candles[0].OpenTimeUtc, candles[^1].OpenTimeUtc,
            candles.Count, fingerprint ?? HistoricalCandleFingerprint.Compute(candles), "fixture-v1",
            createdAtUtc ?? s_start.AddHours(1));

    private static BacktestConfiguration Configuration(HistoricalDataset dataset, int warmup) =>
        new("test-approved-v1", dataset.Symbol, dataset.FromUtc, dataset.ToUtc, warmup, 120m, 0m, 0m);

    private static StrategyParameterSet Parameters() => new(Array.Empty<StrategyParameterDefinition>());

    private abstract class ApprovedTestStrategy : ApprovedStrategyTemplate, IStrategy
    {
        protected ApprovedTestStrategy()
            : base("test-approved-v1", "Test approved", TradingProductType.Spot, "test", "none", "closed candles", "none")
        {
        }

        public StrategyTemplateId TemplateId { get; } = new("test-approved-v1");
        public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => Array.Empty<StrategyParameterDefinition>();
        public abstract StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input);
    }

    private sealed class LifecycleStrategy : ApprovedTestStrategy
    {
        public List<int> VisibleCounts { get; } = [];
        public List<int> VisibleCountsAtProposal { get; } = [];

        public override StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
        {
            VisibleCounts.Add(input.ClosedCandles.Count);
            Assert.DoesNotContain(input.ClosedCandles, candle => candle.CloseTimeUtc > input.AsOfUtc);
            VisibleCountsAtProposal.Add(input.ClosedCandles.Count);
            var direction = input.ClosedCandles.Count switch
            {
                2 => StrategyAnalysisDirection.Bullish,
                4 => StrategyAnalysisDirection.Bearish,
                _ => StrategyAnalysisDirection.Neutral
            };
            return new StrategyAnalysisProposal(input.TemplateId, input.AsOfUtc, direction, 1m, "fixture");
        }
    }

    private sealed class NeutralStrategy : ApprovedTestStrategy
    {
        public List<int> VisibleCounts { get; } = [];
        public override StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input)
        {
            VisibleCounts.Add(input.ClosedCandles.Count);
            return new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Neutral, 0m, "neutral");
        }
    }

    private sealed class RepeatedBullishStrategy : ApprovedTestStrategy
    {
        public override StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input) =>
            new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Bullish, 1m, "bullish");
    }

    private sealed class UnapprovedStrategy : IStrategy
    {
        public StrategyTemplateId TemplateId { get; } = new("test-approved-v1");
        public IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions => Array.Empty<StrategyParameterDefinition>();
        public StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input) =>
            new(input.TemplateId, input.AsOfUtc, StrategyAnalysisDirection.Neutral, 0m, "neutral");
    }
}
