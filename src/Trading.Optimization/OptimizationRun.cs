namespace Trading.Optimization;

using Trading.Strategies;

/// <summary>
/// Evaluates a candidate parameter set against a single dataset split.
/// Implementations must only read data contained inside the supplied split.
/// </summary>
public delegate decimal SplitObjective(StrategyParameterSet parameters, DatasetSplit split);

public sealed class OptimizationRunResult
{
    /// <summary>
    /// Mandatory wording attached to every optimization result. Past or simulated
    /// performance is never a promise of future results.
    /// </summary>
    public const string NoGuaranteeDisclaimer =
        "Optimization and backtest results are historical simulations only. They are not a prediction of future results and do not guarantee profit.";

    private readonly List<ParameterSearchCandidate> _rankedCandidates;

    internal OptimizationRunResult(
        string runId,
        StrategyParameterSet selectedParameters,
        decimal trainingScore,
        decimal validationScore,
        decimal holdoutScore,
        IReadOnlyList<ParameterSearchCandidate> rankedCandidates,
        WalkForwardEvaluationResult? walkForward)
    {
        RunId = runId;
        SelectedParameters = selectedParameters;
        TrainingScore = trainingScore;
        ValidationScore = validationScore;
        HoldoutScore = holdoutScore;
        _rankedCandidates = rankedCandidates.ToList();
        WalkForward = walkForward;
    }

    public string RunId { get; }

    public StrategyParameterSet SelectedParameters { get; }

    /// <summary>Score of the selected parameter set on training data.</summary>
    public decimal TrainingScore { get; }

    /// <summary>Score used to select the parameter set. Selection never uses holdout data.</summary>
    public decimal ValidationScore { get; }

    /// <summary>One-time, post-selection verification score on untouched holdout data.</summary>
    public decimal HoldoutScore { get; }

    public IReadOnlyList<ParameterSearchCandidate> RankedCandidates => _rankedCandidates;

    public WalkForwardEvaluationResult? WalkForward { get; }
}

/// <summary>
/// Orchestrates a responsible optimization run: parameters are searched and selected on
/// training and validation data only, the holdout split is locked up front, and the holdout
/// is scored exactly once after selection is final.
/// </summary>
public sealed class OptimizationRun
{
    private readonly List<StrategyParameterDefinition> _definitions;
    private readonly HoldoutVerificationGuard _holdoutGuard;

    public OptimizationRun(
        string id,
        DatasetSplit training,
        DatasetSplit validation,
        DatasetSplit holdout,
        IEnumerable<StrategyParameterDefinition> definitions)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Optimization run id is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(holdout);
        ArgumentNullException.ThrowIfNull(definitions);

        if (training.SplitType != DatasetSplitType.Training)
        {
            throw new ArgumentException("A training split is required.", nameof(training));
        }

        if (validation.SplitType != DatasetSplitType.Validation)
        {
            throw new ArgumentException("A validation split is required.", nameof(validation));
        }

        if (holdout.SplitType != DatasetSplitType.Holdout)
        {
            throw new ArgumentException("A holdout split is required.", nameof(holdout));
        }

        training.ValidateNoFutureLeakage(validation);
        try
        {
            training.ValidateNoFutureLeakage(holdout);
        }
        catch (ArgumentException ex) when (ex.ParamName == "other")
        {
            throw new ArgumentException(
                "All splits in an optimization run must use the same immutable dataset version.",
                nameof(holdout),
                ex);
        }

        validation.ValidateNoFutureLeakage(holdout);

        if (!validation.IsTimeOrderedAfter(training))
        {
            throw new InvalidOperationException("Validation data must come after training data to prevent look-ahead bias.");
        }

        if (!holdout.IsTimeOrderedAfter(validation))
        {
            throw new InvalidOperationException("Holdout data must come after validation data to prevent look-ahead bias.");
        }

        _definitions = definitions.ToList();
        if (_definitions.Count == 0)
        {
            throw new ArgumentException("At least one parameter definition is required.", nameof(definitions));
        }

        Id = id.Trim();
        Training = training;
        Validation = validation;
        Holdout = holdout;

        // Locks the holdout for evaluation only; any attempt to select on it now fails.
        _holdoutGuard = new HoldoutVerificationGuard(holdout);
    }

    public string Id { get; }

    public DatasetSplit Training { get; }

    public DatasetSplit Validation { get; }

    public DatasetSplit Holdout { get; }

    public IReadOnlyList<StrategyParameterDefinition> Definitions => _definitions;

    public bool HoldoutVerified => _holdoutGuard.IsVerified;

    public OptimizationRunResult Execute(
        SplitObjective objective,
        int gridPointsPerParameter = 5,
        WalkForwardEvaluationResult? walkForward = null)
    {
        ArgumentNullException.ThrowIfNull(objective);

        if (_holdoutGuard.IsVerified)
        {
            throw new InvalidOperationException("This optimization run has already verified its holdout and cannot be re-run.");
        }

        _holdoutGuard.RegisterSelectionCandidate(Training);
        _holdoutGuard.RegisterSelectionCandidate(Validation);

        if (walkForward is not null)
        {
            foreach (var fold in walkForward.Folds)
            {
                _holdoutGuard.RegisterSelectionCandidate(fold.TrainingSplit);
                _holdoutGuard.RegisterSelectionCandidate(fold.ValidationSplit);
            }
        }

        // Selection scores are produced on validation data only; the holdout is untouched here.
        var ranked = ParameterSearchEngine.Search(
            _definitions,
            parameters => Score(objective, parameters, Validation),
            gridPointsPerParameter);

        var best = ranked[0];
        var trainingScore = Score(objective, best.Parameters, Training);

        // Selection is final from this point; the holdout may be scored exactly once.
        _holdoutGuard.VerifyOnce();
        var holdoutScore = objective(best.Parameters, Holdout);

        return new OptimizationRunResult(
            Id,
            best.Parameters,
            trainingScore,
            best.ObjectiveScore,
            holdoutScore,
            ranked,
            walkForward);
    }

    private decimal Score(SplitObjective objective, StrategyParameterSet parameters, DatasetSplit split)
    {
        if (split.SplitType == DatasetSplitType.Holdout)
        {
            throw new InvalidOperationException("Holdout data cannot be scored during parameter selection.");
        }

        _holdoutGuard.EnsureSelectionIsForbiddenAfterVerification();
        return objective(parameters, split);
    }
}
