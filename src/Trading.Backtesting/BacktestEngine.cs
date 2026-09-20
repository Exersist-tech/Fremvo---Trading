using System.Collections.ObjectModel;
using Trading.Domain.Market;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.Backtesting;

/// <summary>
/// Deterministic, in-memory, closed-candle simulation. It has no persistence
/// or execution integration. Fees, slippage, and venue filters are deliberately
/// not applied in this phase and must be configured as zero.
/// </summary>
public sealed class BacktestEngine
{
    public static BacktestResult Run(
        HistoricalDataset dataset,
        IReadOnlyList<Candle> candles,
        IStrategy strategy,
        StrategyParameterSet parameters,
        BacktestConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(configuration);

        ValidatePrerequisites(dataset, candles, strategy, parameters, configuration);

        var events = new List<BacktestEvent>();
        var equitySnapshots = new List<BacktestEquitySnapshot>();
        var cash = configuration.InitialCapital;
        var baseQuantity = 0m;
        var lifecycleComplete = false;

        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            if (index >= configuration.WarmupCandles)
            {
                var snapshot = Array.AsReadOnly(candles.Take(index + 1).ToArray());
                var state = new StrategyState(
                    strategy.TemplateId,
                    index,
                    candle.CloseTimeUtc);
                var proposal = strategy.Evaluate(new StrategyEvaluationInput(
                    strategy.TemplateId,
                    parameters,
                    state,
                    snapshot));

                if (!proposal.TemplateId.Equals(strategy.TemplateId)
                    || proposal.ObservedAtUtc != candle.CloseTimeUtc)
                {
                    throw new InvalidOperationException("Strategy proposal must describe the current closed candle.");
                }

                events.Add(ConvertProposal(proposal, candle, ref cash, ref baseQuantity, ref lifecycleComplete));
            }

            equitySnapshots.Add(new BacktestEquitySnapshot(
                candle.CloseTimeUtc,
                cash,
                baseQuantity,
                candle.Close,
                cash + (baseQuantity * candle.Close)));
        }

        var finalValue = equitySnapshots[^1].Equity;
        return new BacktestResult(
            strategy.TemplateId.Value,
            dataset.Symbol,
            dataset.FromUtc,
            dataset.ToUtc,
            configuration.InitialCapital,
            finalValue,
            finalValue - configuration.InitialCapital,
            0m,
            0m,
            events.Count(@event => @event.Action is BacktestSimulatedAction.Buy or BacktestSimulatedAction.Sell),
            dataset.VersionIdentity,
            events,
            equitySnapshots);
    }

    private static BacktestEvent ConvertProposal(
        StrategyAnalysisProposal proposal,
        Candle candle,
        ref decimal cash,
        ref decimal baseQuantity,
        ref bool lifecycleComplete)
    {
        if (proposal.Direction == StrategyAnalysisDirection.Neutral)
        {
            return new BacktestEvent(candle.CloseTimeUtc, proposal, BacktestSimulatedAction.None,
                "Neutral analysis proposal; no simulated position change.", 0m, candle.Close, cash, baseQuantity);
        }

        if (proposal.Direction == StrategyAnalysisDirection.Bullish
            && baseQuantity == 0m
            && !lifecycleComplete
            && candle.Close > 0m)
        {
            var quantity = cash / candle.Close;
            baseQuantity = quantity;
            cash = 0m;
            return new BacktestEvent(candle.CloseTimeUtc, proposal, BacktestSimulatedAction.Buy,
                "Accepted approved bullish analysis as one full-cash simulated spot entry.", quantity, candle.Close, cash, baseQuantity);
        }

        if (proposal.Direction == StrategyAnalysisDirection.Bearish && baseQuantity > 0m)
        {
            var quantity = baseQuantity;
            cash += quantity * candle.Close;
            baseQuantity = 0m;
            lifecycleComplete = true;
            return new BacktestEvent(candle.CloseTimeUtc, proposal, BacktestSimulatedAction.Sell,
                "Accepted approved bearish analysis as the single simulated spot exit.", quantity, candle.Close, cash, baseQuantity);
        }

        var rationale = proposal.Direction == StrategyAnalysisDirection.Bullish && candle.Close <= 0m
            ? "Rejected bullish analysis because a simulated spot entry requires a positive closed price."
            : proposal.Direction == StrategyAnalysisDirection.Bearish
            ? "Rejected bearish analysis because no spot position exists; shorting is not supported."
            : "Rejected bullish analysis because accumulation and additional lifecycles are not supported.";
        return new BacktestEvent(candle.CloseTimeUtc, proposal, BacktestSimulatedAction.Rejected,
            rationale, 0m, candle.Close, cash, baseQuantity);
    }

    private static void ValidatePrerequisites(
        HistoricalDataset dataset,
        IReadOnlyList<Candle> candles,
        IStrategy strategy,
        StrategyParameterSet parameters,
        BacktestConfiguration configuration)
    {
        if (strategy is not ApprovedStrategyTemplate)
        {
            throw new ArgumentException("Only a platform-approved strategy template can be backtested.", nameof(strategy));
        }

        if (!string.Equals(configuration.StrategyId, strategy.TemplateId.Value, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(configuration.Symbol, dataset.Symbol, StringComparison.Ordinal))
        {
            throw new ArgumentException("Configuration must match the approved strategy and dataset.");
        }

        if (configuration.FromUtc.ToUniversalTime() != dataset.FromUtc
            || configuration.ToUtc.ToUniversalTime() != dataset.ToUtc)
        {
            throw new ArgumentException("Configuration range must exactly match the immutable dataset range.");
        }

        if (configuration.CommissionRate != 0m || configuration.SlippageRate != 0m)
        {
            throw new ArgumentException("Fees and slippage are not applied by the Phase 5.5 engine.");
        }

        if (parameters.Values.Count != strategy.ParameterDefinitions.Count)
        {
            throw new ArgumentException("Parameters must be the complete approved strategy parameter set.", nameof(parameters));
        }

        ValidateCandles(dataset, candles);
    }

    private static void ValidateCandles(HistoricalDataset dataset, IReadOnlyList<Candle> candles)
    {
        if (candles.Count != dataset.CandleCount)
        {
            throw new ArgumentException("Supplied candle count must exactly match the dataset manifest.", nameof(candles));
        }

        var interval = ToCandleInterval(dataset.Interval);
        var duration = TimeSpan.FromMinutes((int)interval);
        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index] ?? throw new ArgumentException("Candle collection cannot contain null values.", nameof(candles));
            if (!candle.IsClosed || !candle.CanBeUsedForClosedCandleSignal
                || candle.CloseTimeUtc > dataset.CreatedAtUtc)
            {
                throw new ArgumentException("All candle evidence must be closed, safe, and available at dataset creation.", nameof(candles));
            }

            if (!string.Equals(candle.Symbol, dataset.Symbol, StringComparison.Ordinal)
                || candle.Interval != interval
                || candle.OpenTimeUtc < dataset.FromUtc
                || candle.OpenTimeUtc > dataset.ToUtc)
            {
                throw new ArgumentException("Candle scope must exactly match the dataset manifest.", nameof(candles));
            }

            if (index == 0 && candle.OpenTimeUtc != dataset.FromUtc
                || index == candles.Count - 1 && candle.OpenTimeUtc != dataset.ToUtc)
            {
                throw new ArgumentException("Candle range must exactly match the dataset manifest.", nameof(candles));
            }

            if (index > 0)
            {
                var previous = candles[index - 1];
                if (candle.OpenTimeUtc <= previous.OpenTimeUtc
                    || candle.CloseTimeUtc <= previous.CloseTimeUtc
                    || candle.OpenTimeUtc != previous.OpenTimeUtc + duration)
                {
                    throw new ArgumentException("Candles must be complete, chronological, unique, and contiguous.", nameof(candles));
                }
            }
        }

        if (!string.Equals(HistoricalCandleFingerprint.Compute(candles), dataset.ContentFingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException("Supplied candle content does not match the immutable dataset fingerprint.", nameof(candles));
        }
    }

    private static CandleInterval ToCandleInterval(string interval) => interval switch
    {
        "1M" => CandleInterval.OneMinute,
        "5M" => CandleInterval.FiveMinutes,
        "10M" => CandleInterval.TenMinutes,
        "15M" => CandleInterval.FifteenMinutes,
        "30M" => CandleInterval.ThirtyMinutes,
        "1H" => CandleInterval.OneHour,
        "4H" => CandleInterval.FourHours,
        "1D" => CandleInterval.OneDay,
        _ => throw new ArgumentOutOfRangeException(nameof(interval))
    };
}

public enum BacktestSimulatedAction { None, Buy, Sell, Rejected }

public sealed record BacktestEvent(
    DateTimeOffset ObservedAtUtc,
    StrategyAnalysisProposal Proposal,
    BacktestSimulatedAction Action,
    string Rationale,
    decimal Quantity,
    decimal Price,
    decimal CashBalance,
    decimal BaseQuantity);

public sealed record BacktestEquitySnapshot(
    DateTimeOffset ObservedAtUtc,
    decimal CashBalance,
    decimal BaseQuantity,
    decimal MarkPrice,
    decimal Equity);
