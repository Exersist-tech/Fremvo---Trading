using System.Collections.ObjectModel;
using Trading.MarketData;
using Trading.Strategies;

namespace Trading.Backtesting;

/// <summary>
/// Deterministic, in-memory, closed-candle simulation. It has no persistence
/// or execution integration. It models a single fully-filled spot lifecycle
/// using only the explicitly configured quote-currency costs and venue filters.
/// Next-candle-open mode avoids signal-close fills but an OHLC open is not an
/// executable quote or a replay of the worker's multi-timeframe rules.
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
        var totalFees = 0m;
        var totalSlippage = 0m;
        var lifecycleComplete = false;
        StrategyAnalysisProposal? pendingProposal = null;

        for (var index = 0; index < candles.Count; index++)
        {
            var candle = candles[index];
            if (pendingProposal is not null)
            {
                events.Add(ConvertProposal(
                    pendingProposal, candle, candle.Open, candle.OpenTimeUtc, configuration,
                    ref cash, ref baseQuantity, ref totalFees, ref totalSlippage, ref lifecycleComplete));
                pendingProposal = null;
            }

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

                if (configuration.FillTiming == BacktestFillTiming.NextCandleOpen
                    && proposal.Direction != StrategyAnalysisDirection.Neutral)
                {
                    if (index == candles.Count - 1)
                    {
                        events.Add(new BacktestEvent(
                            candle.CloseTimeUtc, proposal, BacktestSimulatedAction.Rejected,
                            "No later candle exists in the dataset; a signal cannot be filled at its own close.",
                            0m, candle.Close, cash, baseQuantity, candle.Close));
                    }
                    else
                    {
                        pendingProposal = proposal;
                    }
                }
                else
                {
                    events.Add(ConvertProposal(
                        proposal, candle, candle.Close, candle.CloseTimeUtc, configuration,
                        ref cash, ref baseQuantity, ref totalFees, ref totalSlippage, ref lifecycleComplete));
                }
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
            totalFees,
            totalSlippage,
            events.Count(@event => @event.Action is BacktestSimulatedAction.Buy or BacktestSimulatedAction.Sell),
            dataset.VersionIdentity,
            events,
            equitySnapshots,
            configuration.FillTiming);
    }

    private static BacktestEvent ConvertProposal(
        StrategyAnalysisProposal proposal,
        Candle candle,
        decimal referencePrice,
        DateTimeOffset eventTimeUtc,
        BacktestConfiguration configuration,
        ref decimal cash,
        ref decimal baseQuantity,
        ref decimal totalFees,
        ref decimal totalSlippage,
        ref bool lifecycleComplete)
    {
        if (proposal.Direction == StrategyAnalysisDirection.Neutral)
        {
            return new BacktestEvent(eventTimeUtc, proposal, BacktestSimulatedAction.None,
                "Neutral analysis proposal; no simulated position change.", 0m, referencePrice, cash, baseQuantity);
        }

        if (proposal.Direction == StrategyAnalysisDirection.Bullish
            && baseQuantity == 0m
            && !lifecycleComplete
            && referencePrice > 0m)
        {
            if (!TryGetExecutionPrice(referencePrice, isBuy: true, configuration.SlippageModel, out var price, out var priceRejection))
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, priceRejection!, cash, baseQuantity);
            }

            if (configuration.ExchangeFilter.GetRejectionReason(1m, price) is { } priceFilterRejection
                && priceFilterRejection.Contains("price tick", StringComparison.Ordinal))
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, priceFilterRejection, cash, baseQuantity, price);
            }

            var unconstrainedQuantity = cash / (price * (1m + configuration.FeeModel.TakerFeeRate));
            var quantity = MaximumAffordableQuantity(cash, price, configuration.FeeModel, configuration.ExchangeFilter.StepSize);
            if (quantity <= 0m)
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, "Available cash cannot fund one conforming quantity step including quote fees.",
                    cash, baseQuantity, price);
            }

            if (configuration.ExchangeFilter.GetRejectionReason(quantity, price) is { } filterRejection)
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, filterRejection, cash, baseQuantity, price);
            }

            var notional = quantity * price;
            var fee = configuration.FeeModel.ComputeFee(notional, isMakerOrder: false);
            var debit = notional + fee;
            if (debit > cash)
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, "Conforming quantity would exceed available cash after quote fees.",
                    cash, baseQuantity, price);
            }

            baseQuantity = quantity;
            cash -= debit;
            totalFees += fee;
            var slippage = (price - referencePrice) * quantity;
            totalSlippage += slippage;
            var sizingExplanation = quantity < unconstrainedQuantity
                ? "quantity was conservatively reduced to the configured step without exceeding cash."
                : "quantity exactly matched the configured step.";
            return new BacktestEvent(eventTimeUtc, proposal, BacktestSimulatedAction.Buy,
                $"Accepted approved bullish analysis as one fully-filled spot entry; {sizingExplanation}",
                quantity, price, cash, baseQuantity, referencePrice, fee, slippage);
        }

        if (proposal.Direction == StrategyAnalysisDirection.Bearish && baseQuantity > 0m)
        {
            if (!TryGetExecutionPrice(referencePrice, isBuy: false, configuration.SlippageModel, out var price, out var priceRejection))
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, priceRejection!, cash, baseQuantity);
            }

            var quantity = baseQuantity;
            if (configuration.ExchangeFilter.GetRejectionReason(quantity, price) is { } filterRejection)
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, filterRejection, cash, baseQuantity, price);
            }

            var notional = quantity * price;
            var fee = configuration.FeeModel.ComputeFee(notional, isMakerOrder: false);
            if (fee > notional)
            {
                return Rejected(proposal, referencePrice, eventTimeUtc, "Quote fee exceeds sale proceeds; simulated balance cannot become negative.",
                    cash, baseQuantity, price);
            }

            var proceeds = notional - fee;
            cash += proceeds;
            baseQuantity = 0m;
            totalFees += fee;
            var slippage = (referencePrice - price) * quantity;
            totalSlippage += slippage;
            lifecycleComplete = true;
            return new BacktestEvent(eventTimeUtc, proposal, BacktestSimulatedAction.Sell,
                "Accepted approved bearish analysis as the single fully-filled simulated spot exit.",
                quantity, price, cash, baseQuantity, referencePrice, fee, slippage);
        }

        var rationale = proposal.Direction == StrategyAnalysisDirection.Bullish && referencePrice <= 0m
            ? "Rejected bullish analysis because a simulated spot entry requires a positive reference price."
            : proposal.Direction == StrategyAnalysisDirection.Bearish
            ? "Rejected bearish analysis because no spot position exists; shorting is not supported."
            : "Rejected bullish analysis because accumulation and additional lifecycles are not supported.";
        return new BacktestEvent(eventTimeUtc, proposal, BacktestSimulatedAction.Rejected,
            rationale, 0m, referencePrice, cash, baseQuantity);
    }

    private static bool TryGetExecutionPrice(
        decimal referencePrice,
        bool isBuy,
        SlippageModel slippageModel,
        out decimal executionPrice,
        out string? rejection)
    {
        try
        {
            executionPrice = slippageModel.GetExecutionPrice(referencePrice, isBuy);
            rejection = null;
            return true;
        }
        catch (InvalidOperationException exception)
        {
            executionPrice = referencePrice;
            rejection = exception.Message;
            return false;
        }
    }

    private static decimal MaximumAffordableQuantity(
        decimal cash,
        decimal price,
        FeeModel feeModel,
        decimal stepSize)
    {
        var quantity = FloorToStep(cash / (price * (1m + feeModel.TakerFeeRate)), stepSize);
        var fee = feeModel.ComputeFee(quantity * price, isMakerOrder: false);
        if (quantity > 0m && (quantity * price) + fee > cash)
        {
            quantity = FloorToStep((cash - feeModel.MinimumFee) / price, stepSize);
        }

        return quantity;
    }

    private static decimal FloorToStep(decimal value, decimal stepSize) =>
        value <= 0m ? 0m : value - (value % stepSize);

    private static BacktestEvent Rejected(
        StrategyAnalysisProposal proposal,
        decimal referencePrice,
        DateTimeOffset eventTimeUtc,
        string rationale,
        decimal cash,
        decimal baseQuantity,
        decimal? price = null) =>
        new(eventTimeUtc, proposal, BacktestSimulatedAction.Rejected,
            $"Rejected simulated trade: {rationale}", 0m, price ?? referencePrice, cash, baseQuantity,
            referencePrice);

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

        if (parameters.Values.Count != strategy.ParameterDefinitions.Count)
        {
            throw new ArgumentException("Parameters must be the complete approved strategy parameter set.", nameof(parameters));
        }

        if (configuration.WarmupCandles >= candles.Count)
        {
            throw new ArgumentException("The dataset must contain a closed signal candle after warmup.", nameof(candles));
        }

        HistoricalCandleEvidenceValidator.Validate(dataset, candles);
    }
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
    decimal BaseQuantity,
    decimal? ReferencePrice = null,
    decimal Fee = 0m,
    decimal Slippage = 0m);

public sealed record BacktestEquitySnapshot(
    DateTimeOffset ObservedAtUtc,
    decimal CashBalance,
    decimal BaseQuantity,
    decimal MarkPrice,
    decimal Equity);
