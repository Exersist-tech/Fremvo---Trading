namespace Trading.Strategies;

/// <summary>
/// An exchange-neutral analysis observation. It contains no account, order,
/// price, quantity, or execution fields and cannot be submitted to a venue.
/// </summary>
public sealed class StrategyAnalysisProposal
{
    public StrategyAnalysisProposal(
        StrategyTemplateId templateId,
        DateTimeOffset observedAtUtc,
        StrategyAnalysisDirection direction,
        decimal confidence,
        string rationale)
    {
        ArgumentNullException.ThrowIfNull(templateId);

        if (observedAtUtc == default)
        {
            throw new ArgumentException("Observation timestamp is required.", nameof(observedAtUtc));
        }

        if (confidence < 0m || confidence > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be between zero and one.");
        }

        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new ArgumentException("Rationale is required.", nameof(rationale));
        }

        TemplateId = templateId;
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        Direction = direction;
        Confidence = confidence;
        Rationale = rationale.Trim();
    }

    public StrategyTemplateId TemplateId { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public StrategyAnalysisDirection Direction { get; }

    public decimal Confidence { get; }

    public string Rationale { get; }
}
