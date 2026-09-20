namespace Trading.Optimization;

using System.Collections.ObjectModel;
using System.Globalization;
using Trading.Strategies;

/// <summary>
/// Immutable evidence for one chronologically isolated, decimal-scored walk-forward fold.
/// It represents historical evaluation only and is not a profitability claim.
/// </summary>
public sealed class WalkForwardFold
{
    public WalkForwardFold(
        string id,
        DatasetSplit trainingSplit,
        DatasetSplit validationSplit,
        StrategyParameterSet parameters,
        decimal objectiveValue,
        string objectiveId = "unspecified-decimal-objective")
    {
        Id = Required(id, nameof(id));
        ObjectiveId = Required(objectiveId, nameof(objectiveId));
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
        trainingSplit.ValidateNoFutureLeakage(validationSplit);
        if (!validationSplit.IsTimeOrderedAfter(trainingSplit))
        {
            throw new InvalidOperationException("Walk-forward validation must begin after its training window to avoid future-data leakage.");
        }

        TrainingSplit = trainingSplit;
        ValidationSplit = validationSplit;
        Parameters = parameters;
        ObjectiveValue = objectiveValue;
        ParameterIdentity = CreateParameterIdentity(parameters);
    }

    public string Id { get; }

    public string ObjectiveId { get; }

    public DatasetSplit TrainingSplit { get; }

    public DatasetSplit ValidationSplit { get; }

    public StrategyParameterSet Parameters { get; }

    /// <summary>Stable invariant-culture identity for the immutable parameter values used by this fold.</summary>
    public string ParameterIdentity { get; }

    /// <summary>A caller-supplied decimal objective value, not a profitability claim.</summary>
    public decimal ObjectiveValue { get; }

    /// <summary>Compatibility alias for the decimal objective value.</summary>
    public decimal Score => ObjectiveValue;

    private static string Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        return value.Trim();
    }

    private static string CreateParameterIdentity(StrategyParameterSet parameters) =>
        string.Join(
            "\u001f",
            parameters.Values
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .Select(value => $"{value.Key}\u001e{value.Value.ToString("G29", CultureInfo.InvariantCulture)}"));
}

/// <summary>Deterministic decimal aggregate metrics for a completed walk-forward report.</summary>
public sealed class WalkForwardAggregateMetrics
{
    internal WalkForwardAggregateMetrics(IReadOnlyList<WalkForwardFold> folds)
    {
        FoldCount = folds.Count;
        AverageObjective = folds.Average(fold => fold.ObjectiveValue);
        MinimumObjective = folds.Min(fold => fold.ObjectiveValue);
        MaximumObjective = folds.Max(fold => fold.ObjectiveValue);
    }

    public int FoldCount { get; }

    public decimal AverageObjective { get; }

    public decimal MinimumObjective { get; }

    public decimal MaximumObjective { get; }
}

/// <summary>
/// Immutable chronological report of completed folds. It performs no I/O, execution, or holdout
/// scoring; callers must register each fold with the existing holdout guard before use.
/// </summary>
public sealed class WalkForwardEvaluationResult
{
    public const int MaximumFoldCount = 128;

    private readonly ReadOnlyCollection<WalkForwardFold> _folds;

    public WalkForwardEvaluationResult(IReadOnlyList<WalkForwardFold> folds)
    {
        ArgumentNullException.ThrowIfNull(folds);

        if (folds.Count == 0)
        {
            throw new ArgumentException("At least one fold is required for a walk-forward evaluation.", nameof(folds));
        }

        if (folds.Count > MaximumFoldCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(folds),
                $"At most {MaximumFoldCount} walk-forward folds are supported.");
        }

        if (folds.Any(fold => fold is null))
        {
            throw new ArgumentException("Walk-forward folds cannot contain null values.", nameof(folds));
        }

        var ordered = folds
            .OrderBy(fold => fold.ValidationSplit.FromUtc)
            .ThenBy(fold => fold.ValidationSplit.ToUtc)
            .ThenBy(fold => fold.Id, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Select(fold => fold.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ArgumentException("Walk-forward fold identifiers must be unique.", nameof(folds));
        }

        if (ordered.Select(fold => fold.ObjectiveId).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            throw new ArgumentException("All walk-forward folds must use the same objective identity.", nameof(folds));
        }

        ValidateChronology(ordered);

        _folds = Array.AsReadOnly(ordered);
        AggregateMetrics = new WalkForwardAggregateMetrics(_folds);
    }

    public IReadOnlyList<WalkForwardFold> Folds => _folds;

    public int FoldCount => AggregateMetrics.FoldCount;

    public WalkForwardAggregateMetrics AggregateMetrics { get; }

    public decimal AverageScore => AggregateMetrics.AverageObjective;

    public WalkForwardFold BestFold => _folds
        .OrderByDescending(fold => fold.ObjectiveValue)
        .ThenBy(fold => fold.ValidationSplit.FromUtc)
        .ThenBy(fold => fold.Id, StringComparer.Ordinal)
        .First();

    public string ToSummary()
    {
        var best = BestFold;
        return $"{FoldCount} walk-forward folds; average objective {AverageScore:F6}; best fold {best.Id} objective {best.ObjectiveValue:F6}. Historical evaluation only; not a profitability claim.";
    }

    private static void ValidateChronology(WalkForwardFold[] ordered)
    {
        var reference = ordered[0].ValidationSplit;
        foreach (var fold in ordered)
        {
            reference.EnsureSameDataset(fold.TrainingSplit);
            reference.EnsureSameDataset(fold.ValidationSplit);
        }

        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];

            if (!current.ValidationSplit.IsTimeOrderedAfter(previous.ValidationSplit))
            {
                throw new InvalidOperationException("Walk-forward validation windows must be chronologically ordered and non-overlapping.");
            }

            // A fold may train on an earlier fold's completed validation history, but never on a
            // validation window that lies ahead of its own validation window.
            if (current.TrainingSplit.ToUtc > current.ValidationSplit.FromUtc)
            {
                throw new InvalidOperationException("Walk-forward training cannot peek into its validation window.");
            }
        }
    }
}
