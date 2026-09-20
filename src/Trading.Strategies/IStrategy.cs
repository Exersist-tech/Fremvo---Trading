namespace Trading.Strategies;

/// <summary>
/// Implemented only by platform-authored, server-registered templates.
/// Evaluation is pure analysis: it accepts an immutable snapshot and returns
/// a non-executable proposal without connector or execution-service access.
/// </summary>
public interface IStrategy
{
    StrategyTemplateId TemplateId { get; }

    IReadOnlyCollection<StrategyParameterDefinition> ParameterDefinitions { get; }

    StrategyAnalysisProposal Evaluate(StrategyEvaluationInput input);
}
