namespace Trading.Optimization;

using Trading.Strategies;

public sealed class WalkForwardFold
{
    public WalkForwardFold(
        string id,
        DatasetSplit trainingSplit,
        DatasetSplit validationSplit,
        StrategyParameterSet parameters,
        decimal score)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Fold id is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(trainingSplit);
        ArgumentNullException.ThrowIfNull(validationSplit);
        ArgumentNullException.ThrowIfNull(parameters);

        if (trainingSplit.SplitType != DatasetSplitType.Training)
        {
            throw new ArgumentException("Training fold must use a training split.", nameof(trainingSplit));
        }

        if (validationSplit.SplitType != DatasetSplitType.Validation)
        {
            throw new ArgumentException("Validation fold must use a validation split.", nameof(validationSplit));
        }

        trainingSplit.EnsureSameDataset(validationSplit);

        if (!validationSplit.IsTimeOrderedAfter(trainingSplit))
        {
            throw new InvalidOperationException("Walk-forward validation folds must begin after the training split ends to avoid future-data leakage.");
        }

        Id = id.Trim();
        TrainingSplit = trainingSplit;
        ValidationSplit = validationSplit;
        Parameters = parameters;
        Score = score;
    }

    public string Id { get; }

    public DatasetSplit TrainingSplit { get; }

    public DatasetSplit ValidationSplit { get; }

    public StrategyParameterSet Parameters { get; }

    public decimal Score { get; }
}

public sealed class WalkForwardEvaluationResult
{
    private readonly List<WalkForwardFold> _folds;

    public WalkForwardEvaluationResult(IReadOnlyList<WalkForwardFold> folds)
    {
        ArgumentNullException.ThrowIfNull(folds);

        if (folds.Count == 0)
        {
            throw new ArgumentException("At least one fold is required for a walk-forward evaluation.", nameof(folds));
        }

        var ordered = folds
            .OrderBy(fold => fold.ValidationSplit.FromUtc)
            .ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];

            current.ValidationSplit.EnsureSameDataset(previous.ValidationSplit);
            if (!current.ValidationSplit.IsTimeOrderedAfter(previous.ValidationSplit))
            {
                throw new InvalidOperationException("Walk-forward validation folds must be non-overlapping and time-ordered.");
            }
        }

        _folds = ordered;
    }

    public IReadOnlyList<WalkForwardFold> Folds => _folds;

    public int FoldCount => _folds.Count;

    public decimal AverageScore => _folds.Average(fold => fold.Score);

    public WalkForwardFold BestFold => _folds.OrderByDescending(fold => fold.Score).First();

    public string ToSummary()
    {
        var best = BestFold;
        return $"{FoldCount} walk-forward folds; average score {AverageScore:F6}; best fold {best.Id} scored {best.Score:F6}.";
    }
}
