namespace Trading.Risk;

/// <summary>
/// Same-quote, exchange-observed evidence for increasing exposure in a linear futures contract.
/// GrossNotional includes all existing account positions in this quote currency.
/// ContractMultiplier is the verified base-asset amount represented by one
/// linear contract, not a user-controlled sizing parameter.
/// </summary>
public sealed record FuturesExposureEvidence(
    decimal AvailableMargin,
    decimal GrossNotional,
    decimal ObservedLeverage,
    decimal MarkPrice,
    DateTimeOffset? AccountObservedAtUtc,
    DateTimeOffset? PositionsObservedAtUtc,
    DateTimeOffset? MarketObservedAtUtc,
    bool HasUnresolvedOrders,
    string QuoteCurrency,
    decimal ContractMultiplier);

public sealed class FuturesExposureRiskEvaluator
{
    private readonly decimal _maxLeverage;
    private readonly decimal _maxGrossNotional;
    private readonly decimal _maxOrderNotional;
    private readonly decimal _maxPriceDeviation;
    private readonly StalenessPolicy _staleness;

    public FuturesExposureRiskEvaluator(
        decimal maxLeverage,
        decimal maxGrossNotional,
        decimal maxOrderNotional,
        TimeSpan maxEvidenceAge,
        decimal maxPriceDeviation = 0.02m)
    {
        if (maxLeverage is < 1m or > 2m)
            throw new ArgumentOutOfRangeException(nameof(maxLeverage), "Futures leverage is capped at 2x.");
        if (maxGrossNotional <= 0m || maxOrderNotional <= 0m
            || maxOrderNotional > maxGrossNotional)
            throw new ArgumentOutOfRangeException(nameof(maxOrderNotional),
                "Positive order and account notional ceilings are required.");
        if (maxEvidenceAge <= TimeSpan.Zero || maxEvidenceAge > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(maxEvidenceAge),
                "Futures exposure evidence must be no older than 30 seconds.");
        if (maxPriceDeviation is <= 0m or > 0.02m)
            throw new ArgumentOutOfRangeException(nameof(maxPriceDeviation),
                "A limit price must remain within 2% of the current mark.");
        _maxLeverage = maxLeverage;
        _maxGrossNotional = maxGrossNotional;
        _maxOrderNotional = maxOrderNotional;
        _maxPriceDeviation = maxPriceDeviation;
        _staleness = new StalenessPolicy(maxEvidenceAge);
    }

    public RiskEvaluationResult EvaluateIncrease(
        FuturesExposureEvidence evidence,
        string orderQuoteCurrency,
        decimal quantity,
        decimal limitPrice,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (nowUtc.Offset != TimeSpan.Zero)
            return Deny("The evaluation instant must be UTC.");
        if (evidence.HasUnresolvedOrders)
            return Deny("Unresolved futures orders must be reconciled before increasing exposure.");
        if (_staleness.IsStale(evidence.AccountObservedAtUtc, nowUtc)
            || _staleness.IsStale(evidence.PositionsObservedAtUtc, nowUtc)
            || _staleness.IsStale(evidence.MarketObservedAtUtc, nowUtc))
            return Deny("Current account, position and market evidence is required.");
        if (string.IsNullOrWhiteSpace(orderQuoteCurrency)
            || !string.Equals(evidence.QuoteCurrency, orderQuoteCurrency, StringComparison.OrdinalIgnoreCase))
            return Deny("Account exposure and the order must use the same quote currency.");
        if (quantity <= 0m || limitPrice <= 0m || evidence.MarkPrice <= 0m
            || evidence.ContractMultiplier <= 0m
            || evidence.AvailableMargin < 0m || evidence.GrossNotional < 0m)
            return Deny("Positive order prices, contract multiplier, quantity and valid account evidence are required.");
        if (evidence.ObservedLeverage < 1m || evidence.ObservedLeverage > _maxLeverage)
            return Deny("Observed account leverage exceeds the mandatory ceiling or is unavailable.");

        try
        {
            var orderNotional = quantity * evidence.ContractMultiplier * limitPrice;
            if (orderNotional > _maxOrderNotional
                || evidence.GrossNotional + orderNotional > _maxGrossNotional)
                return Deny("The order or total account exposure exceeds its mandatory notional ceiling.");
            if (Math.Abs(limitPrice - evidence.MarkPrice) / evidence.MarkPrice > _maxPriceDeviation)
                return Deny("The order limit price diverges from the current mark.");
            if (orderNotional / evidence.ObservedLeverage > evidence.AvailableMargin)
                return Deny("Current available margin is insufficient without increasing leverage.");
        }
        catch (OverflowException)
        {
            return Deny("Futures exposure arithmetic exceeded its safe decimal range.");
        }
        return new RiskEvaluationResult(true);
    }

    private static RiskEvaluationResult Deny(string reason) => new(false, reason);
}
