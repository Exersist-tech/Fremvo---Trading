namespace Trading.Domain.Entitlements;

public sealed class Plan
{
    public Plan(Guid id, string code, string name, int maxExperimentWorkers,
        bool liveTradingEligible = false, bool futuresEligible = false)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A plan id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(code) || code.Length > 40
            || !code.All(character => char.IsAsciiLetterUpper(character)
                || char.IsAsciiDigit(character) || character == '-'))
            throw new ArgumentException("A plan code must be uppercase ASCII letters, digits or hyphens.", nameof(code));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
            throw new ArgumentException("A short plan name is required.", nameof(name));
        if (maxExperimentWorkers is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxExperimentWorkers));
        if (futuresEligible && !liveTradingEligible)
            throw new ArgumentException("Futures eligibility requires a separate live eligibility flag.", nameof(futuresEligible));
        Id = id;
        Code = code;
        Name = name.Trim();
        MaxExperimentWorkers = maxExperimentWorkers;
        LiveTradingEligible = liveTradingEligible;
        FuturesEligible = futuresEligible;
    }

    public Guid Id { get; }
    public string Code { get; }
    public string Name { get; }
    public int MaxExperimentWorkers { get; }
    public bool LiveTradingEligible { get; }
    public bool FuturesEligible { get; }
}
