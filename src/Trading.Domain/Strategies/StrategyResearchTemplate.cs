namespace Trading.Domain.Strategies;

public sealed class StrategyResearchTemplate
{
    public StrategyResearchTemplate(
        string id,
        string name,
        string researchHypothesis,
        TradingProductType supportedProductType,
        string supportedInstruments,
        string regimeRequirements,
        string regimeTimeframe,
        string signalTimeframe,
        string executionTimeframe,
        int requiredWarmup,
        string parameterDefinitions,
        string defaultResearchRanges,
        string entryConditions,
        string exitConditions,
        string invalidationConditions,
        string positionSizingInterface,
        string feeAndSlippageAssumptions,
        string staleDataBehavior,
        string expectedWeaknesses,
        string rejectionCriteria,
        string requiredUnitTests,
        string requiredBacktests,
        string paperTradingEligibility,
        string liveApprovalStatus)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(researchHypothesis);
        ArgumentNullException.ThrowIfNull(supportedInstruments);
        ArgumentNullException.ThrowIfNull(regimeRequirements);
        ArgumentNullException.ThrowIfNull(regimeTimeframe);
        ArgumentNullException.ThrowIfNull(signalTimeframe);
        ArgumentNullException.ThrowIfNull(executionTimeframe);
        ArgumentNullException.ThrowIfNull(parameterDefinitions);
        ArgumentNullException.ThrowIfNull(defaultResearchRanges);
        ArgumentNullException.ThrowIfNull(entryConditions);
        ArgumentNullException.ThrowIfNull(exitConditions);
        ArgumentNullException.ThrowIfNull(invalidationConditions);
        ArgumentNullException.ThrowIfNull(positionSizingInterface);
        ArgumentNullException.ThrowIfNull(feeAndSlippageAssumptions);
        ArgumentNullException.ThrowIfNull(staleDataBehavior);
        ArgumentNullException.ThrowIfNull(expectedWeaknesses);
        ArgumentNullException.ThrowIfNull(rejectionCriteria);
        ArgumentNullException.ThrowIfNull(requiredUnitTests);
        ArgumentNullException.ThrowIfNull(requiredBacktests);
        ArgumentNullException.ThrowIfNull(paperTradingEligibility);
        ArgumentNullException.ThrowIfNull(liveApprovalStatus);

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Strategy id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Strategy name is required.", nameof(name));
        }

        if (requiredWarmup < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredWarmup), "Required warmup cannot be negative.");
        }

        Id = id.Trim();
        Name = name.Trim();
        ResearchHypothesis = researchHypothesis.Trim();
        SupportedProductType = supportedProductType;
        SupportedInstruments = supportedInstruments.Trim();
        RegimeRequirements = regimeRequirements.Trim();
        RegimeTimeframe = regimeTimeframe.Trim();
        SignalTimeframe = signalTimeframe.Trim();
        ExecutionTimeframe = executionTimeframe.Trim();
        RequiredWarmup = requiredWarmup;
        ParameterDefinitions = parameterDefinitions.Trim();
        DefaultResearchRanges = defaultResearchRanges.Trim();
        EntryConditions = entryConditions.Trim();
        ExitConditions = exitConditions.Trim();
        InvalidationConditions = invalidationConditions.Trim();
        PositionSizingInterface = positionSizingInterface.Trim();
        FeeAndSlippageAssumptions = feeAndSlippageAssumptions.Trim();
        StaleDataBehavior = staleDataBehavior.Trim();
        ExpectedWeaknesses = expectedWeaknesses.Trim();
        RejectionCriteria = rejectionCriteria.Trim();
        RequiredUnitTests = requiredUnitTests.Trim();
        RequiredBacktests = requiredBacktests.Trim();
        PaperTradingEligibility = paperTradingEligibility.Trim();
        LiveApprovalStatus = liveApprovalStatus.Trim();
    }

    public string Id { get; }

    public string Name { get; }

    public string ResearchHypothesis { get; }

    public TradingProductType SupportedProductType { get; }

    public string SupportedInstruments { get; }

    public string RegimeRequirements { get; }

    public string RegimeTimeframe { get; }

    public string SignalTimeframe { get; }

    public string ExecutionTimeframe { get; }

    public int RequiredWarmup { get; }

    public string ParameterDefinitions { get; }

    public string DefaultResearchRanges { get; }

    public string EntryConditions { get; }

    public string ExitConditions { get; }

    public string InvalidationConditions { get; }

    public string PositionSizingInterface { get; }

    public string FeeAndSlippageAssumptions { get; }

    public string StaleDataBehavior { get; }

    public string ExpectedWeaknesses { get; }

    public string RejectionCriteria { get; }

    public string RequiredUnitTests { get; }

    public string RequiredBacktests { get; }

    public string PaperTradingEligibility { get; }

    public string LiveApprovalStatus { get; }
}
