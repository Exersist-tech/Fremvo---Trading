namespace Trading.Optimization;

using Trading.Strategies;

/// <summary>
/// A proposed optimization split configuration, before any data is scored.
/// All timestamps are UTC.
/// </summary>
public sealed class OptimizationPlanRequest
{
    public OptimizationPlanRequest(
        string symbol,
        DateTimeOffset trainingFromUtc,
        DateTimeOffset trainingToUtc,
        DateTimeOffset validationFromUtc,
        DateTimeOffset validationToUtc,
        DateTimeOffset holdoutFromUtc,
        DateTimeOffset holdoutToUtc,
        IEnumerable<StrategyParameterDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        Symbol = symbol;
        TrainingFromUtc = trainingFromUtc;
        TrainingToUtc = trainingToUtc;
        ValidationFromUtc = validationFromUtc;
        ValidationToUtc = validationToUtc;
        HoldoutFromUtc = holdoutFromUtc;
        HoldoutToUtc = holdoutToUtc;
        Definitions = definitions.ToList();
    }

    public string Symbol { get; }

    public DateTimeOffset TrainingFromUtc { get; }

    public DateTimeOffset TrainingToUtc { get; }

    public DateTimeOffset ValidationFromUtc { get; }

    public DateTimeOffset ValidationToUtc { get; }

    public DateTimeOffset HoldoutFromUtc { get; }

    public DateTimeOffset HoldoutToUtc { get; }

    public IReadOnlyList<StrategyParameterDefinition> Definitions { get; }
}

public sealed class OptimizationPlanValidationResult
{
    /// <summary>
    /// Reason a validated plan still cannot be scored. Scoring requires a backtest engine,
    /// which is not implemented yet; no simulated or placeholder scores are ever produced.
    /// </summary>
    public const string ScoringEngineUnavailable =
        "Scoring is unavailable until the backtest engine is implemented. Plans can be configured and validated, but not executed.";

    private readonly List<string> _errors;

    private OptimizationPlanValidationResult(bool isValid, IEnumerable<string> errors, string? executionBlockedReason)
    {
        IsValid = isValid;
        _errors = errors.ToList();
        ExecutionBlockedReason = executionBlockedReason;
    }

    public bool IsValid { get; }

    public IReadOnlyList<string> Errors => _errors;

    /// <summary>
    /// Non-null while the plan cannot be executed. Surfaced to the UI so a valid plan is
    /// never mistaken for a completed optimization.
    /// </summary>
    public string? ExecutionBlockedReason { get; }

    /// <summary>
    /// True only when a scoring engine exists to execute the plan.
    /// </summary>
    public bool CanBeExecuted => ExecutionBlockedReason is null;

    public static OptimizationPlanValidationResult Valid() =>
        new(true, Array.Empty<string>(), ScoringEngineUnavailable);

    public static OptimizationPlanValidationResult Invalid(IEnumerable<string> errors) =>
        new(false, errors, ScoringEngineUnavailable);
}

/// <summary>
/// Validates a proposed optimization plan without reading any market data.
/// This is the safety gate that rejects overlapping or out-of-order splits before
/// any scoring could introduce look-ahead bias or holdout leakage.
/// </summary>
public static class OptimizationPlanValidator
{
    public static OptimizationPlanValidationResult Validate(OptimizationPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            errors.Add("Symbol is required.");
        }

        if (request.Definitions.Count == 0)
        {
            errors.Add("At least one parameter definition is required.");
        }

        if (errors.Count > 0)
        {
            return OptimizationPlanValidationResult.Invalid(errors);
        }

        try
        {
            RequireUtc(request.TrainingFromUtc, nameof(request.TrainingFromUtc));
            RequireUtc(request.TrainingToUtc, nameof(request.TrainingToUtc));
            RequireUtc(request.ValidationFromUtc, nameof(request.ValidationFromUtc));
            RequireUtc(request.ValidationToUtc, nameof(request.ValidationToUtc));
            RequireUtc(request.HoldoutFromUtc, nameof(request.HoldoutFromUtc));
            RequireUtc(request.HoldoutToUtc, nameof(request.HoldoutToUtc));

            if (request.TrainingToUtc <= request.TrainingFromUtc ||
                request.ValidationToUtc <= request.ValidationFromUtc ||
                request.HoldoutToUtc <= request.HoldoutFromUtc)
            {
                throw new ArgumentException("Each split end time must be after its start time.");
            }

            if (request.ValidationFromUtc < request.TrainingToUtc ||
                request.HoldoutFromUtc < request.ValidationToUtc)
            {
                throw new InvalidOperationException("Optimization splits must be non-overlapping and time-ordered to prevent look-ahead bias.");
            }
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
        }

        return errors.Count == 0
            ? OptimizationPlanValidationResult.Valid()
            : OptimizationPlanValidationResult.Invalid(errors);
    }

    private static void RequireUtc(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Split timestamps must be expressed explicitly in UTC.", parameterName);
        }
    }
}
