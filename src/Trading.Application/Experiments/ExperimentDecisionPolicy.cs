using System.Collections.Concurrent;
using Trading.Domain.Experiments;
using Trading.Domain.Market;

namespace Trading.Application.Experiments;

/// <summary>Closed-candle identity used to make an experiment decision replay-safe.</summary>
public sealed record ExperimentClosedCandleIdentity(
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset OpenTimeUtc,
    DateTimeOffset CloseTimeUtc,
    DateTimeOffset AsOfUtc);

/// <summary>Runner-attested evidence. Its constructor is internal so callers cannot manufacture approved provenance.</summary>
public sealed class ExperimentDecisionEvidence
{
    internal ExperimentDecisionEvidence(
        Guid userId, Guid workerId, int groupConfigurationVersion, ExperimentResearchGroup group,
        string strategyId, int strategyVersion, string strategyFingerprint, string parametersFingerprint,
        DateTimeOffset asOfUtc, string symbol, CandleInterval interval, DateTimeOffset openTimeUtc,
        DateTimeOffset closeTimeUtc, string contextFingerprint)
    {
        UserId = userId; WorkerId = workerId; GroupConfigurationVersion = groupConfigurationVersion; Group = group;
        StrategyId = strategyId; StrategyVersion = strategyVersion; StrategyFingerprint = strategyFingerprint;
        ParametersFingerprint = parametersFingerprint; AsOfUtc = asOfUtc; Symbol = symbol; Interval = interval;
        OpenTimeUtc = openTimeUtc; CloseTimeUtc = closeTimeUtc; ContextFingerprint = contextFingerprint;
    }

    public Guid UserId { get; }
    public Guid WorkerId { get; }
    public int GroupConfigurationVersion { get; }
    public ExperimentResearchGroup Group { get; }
    public string StrategyId { get; }
    public int StrategyVersion { get; }
    public string StrategyFingerprint { get; }
    public string ParametersFingerprint { get; }
    public DateTimeOffset AsOfUtc { get; }
    public string Symbol { get; }
    public CandleInterval Interval { get; }
    public DateTimeOffset OpenTimeUtc { get; }
    public DateTimeOffset CloseTimeUtc { get; }
    public string ContextFingerprint { get; }

    internal ExperimentDecisionEvidence WithContextFingerprint(string contextFingerprint) =>
        new(
            UserId,
            WorkerId,
            GroupConfigurationVersion,
            Group,
            StrategyId,
            StrategyVersion,
            StrategyFingerprint,
            ParametersFingerprint,
            AsOfUtc,
            Symbol,
            Interval,
            OpenTimeUtc,
            CloseTimeUtc,
            contextFingerprint);
}

/// <summary>Explicit worker state supplied to policy; it is not an account, order, fill, or sizing model.</summary>
public sealed record ExperimentWorkerPortfolioSnapshot(Guid UserId, Guid WorkerId, decimal PositionQuantity, DateTimeOffset AsOfUtc);

public enum ExperimentProposalAction { Neutral = 0, Open, Add, Reduce, Close }

/// <summary>Exchange-neutral research output. It is deliberately neither an intent nor an execution command.</summary>
public sealed record ExperimentProposal(ExperimentProposalAction Action, string Reason);

public sealed record ExperimentDecisionKey(
    Guid UserId, Guid WorkerId, int GroupConfigurationVersion, ExperimentResearchGroup Group,
    string StrategyId, int StrategyVersion, string StrategyFingerprint,
    string Symbol, CandleInterval Interval, DateTimeOffset OpenTimeUtc, DateTimeOffset CloseTimeUtc, DateTimeOffset AsOfUtc);

public sealed record ExperimentDecisionRecord(ExperimentDecisionKey Key, ExperimentProposal Proposal, string EvidenceFingerprint, DateTimeOffset RecordedAtUtc);
public enum ExperimentDecisionWriteResult { Inserted, Duplicate, Conflict }

/// <summary>All reads and writes are owner-scoped. Implementations must preserve the compound identity.</summary>
public interface IExperimentDecisionLedger
{
    Task<(ExperimentDecisionWriteResult Result, ExperimentDecisionRecord? Record)> RecordAsync(
        Guid userId, ExperimentDecisionRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExperimentDecisionRecord>> ListAsync(
        Guid userId, Guid workerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fixed policy mapping for approved experiment observations. It cannot run caller-provided scripts
/// and never computes quantity. Position additions require a separately recorded favorable mark.
/// </summary>
public sealed class ExperimentDecisionPolicy
{
    private readonly IExperimentDecisionLedger _ledger;
    private readonly TimeProvider _timeProvider;

    public ExperimentDecisionPolicy(IExperimentDecisionLedger ledger, TimeProvider? timeProvider = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExperimentDecisionRecord> DecideAsync(
        ExperimentWorker worker, ExperimentResearchGroupConfiguration configuration, ExperimentResearchGroupAssignment assignment,
        ExperimentAnalysisResult analysis, ExperimentWorkerPortfolioSnapshot portfolio, ExperimentClosedCandleIdentity candle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worker); ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(assignment); ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(portfolio); ArgumentNullException.ThrowIfNull(candle);
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = analysis.Evidence ?? throw new InvalidOperationException("Only an approved runner analysis result may reach decision policy.");
        Validate(worker, configuration, assignment, portfolio, candle, evidence);
        var key = new ExperimentDecisionKey(worker.UserId, worker.Id, configuration.Version, assignment.Group,
            evidence.StrategyId, evidence.StrategyVersion, evidence.StrategyFingerprint, candle.Symbol.Trim(), candle.Interval,
            candle.OpenTimeUtc, candle.CloseTimeUtc, candle.AsOfUtc);
        var proposal = Map(worker, analysis, portfolio);
        var fingerprint = $"{analysis.Outcome}|{analysis.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{analysis.Reason}|{evidence.ParametersFingerprint}|{evidence.ContextFingerprint}";
        var candidate = new ExperimentDecisionRecord(key, proposal, fingerprint, _timeProvider.GetUtcNow());
        var write = await _ledger.RecordAsync(worker.UserId, candidate, cancellationToken).ConfigureAwait(false);
        return write.Result switch
        {
            ExperimentDecisionWriteResult.Inserted or ExperimentDecisionWriteResult.Duplicate when write.Record is not null => write.Record,
            ExperimentDecisionWriteResult.Conflict
                when write.Record?.Proposal.Action == ExperimentProposalAction.Neutral => write.Record,
            ExperimentDecisionWriteResult.Conflict => throw new InvalidOperationException("A conflicting decision or evidence record already exists for this worker candle."),
            _ => throw new InvalidOperationException("Decision ledger did not return a record.")
        };
    }

    private void Validate(ExperimentWorker worker, ExperimentResearchGroupConfiguration configuration, ExperimentResearchGroupAssignment assignment,
        ExperimentWorkerPortfolioSnapshot portfolio, ExperimentClosedCandleIdentity candle, ExperimentDecisionEvidence evidence)
    {
        var now = _timeProvider.GetUtcNow();
        if (worker.UserId != configuration.UserId || assignment.UserId != worker.UserId || assignment.WorkerId != worker.Id
            || !configuration.Assignments.Any(item => ReferenceEquals(item, assignment)) || !configuration.IsRunnableFor(worker, assignment)
            || portfolio.UserId != worker.UserId || portfolio.WorkerId != worker.Id || portfolio.PositionQuantity < 0m
            || evidence.UserId != worker.UserId || evidence.WorkerId != worker.Id || evidence.GroupConfigurationVersion != configuration.Version
            || evidence.Group != assignment.Group || !string.Equals(evidence.StrategyId, worker.StrategyId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.ParametersFingerprint, assignment.Provenance.ParametersFingerprint, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(evidence.ContextFingerprint)
            || candle.OpenTimeUtc.Offset != TimeSpan.Zero || candle.CloseTimeUtc.Offset != TimeSpan.Zero || candle.AsOfUtc.Offset != TimeSpan.Zero
            || portfolio.AsOfUtc.Offset != TimeSpan.Zero || evidence.AsOfUtc.Offset != TimeSpan.Zero
            || candle.OpenTimeUtc >= candle.CloseTimeUtc || candle.CloseTimeUtc > candle.AsOfUtc || candle.AsOfUtc > now
            || portfolio.AsOfUtc != candle.AsOfUtc || evidence.AsOfUtc != candle.AsOfUtc
            || candle.Interval == CandleInterval.None || string.IsNullOrWhiteSpace(candle.Symbol)
            || !string.Equals(candle.Symbol, worker.MarketSymbol, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(evidence.Symbol, candle.Symbol, StringComparison.OrdinalIgnoreCase) || evidence.Interval != candle.Interval
            || evidence.OpenTimeUtc != candle.OpenTimeUtc || evidence.CloseTimeUtc != candle.CloseTimeUtc)
        {
            throw new InvalidOperationException("Experiment decision evidence, ownership, portfolio, or closed-candle provenance is invalid.");
        }
    }

    private static ExperimentProposal Map(
        ExperimentWorker worker,
        ExperimentAnalysisResult analysis,
        ExperimentWorkerPortfolioSnapshot portfolio)
    {
        if (analysis.Outcome is ExperimentAnalysisOutcome.Blocked or ExperimentAnalysisOutcome.NoCondition || analysis.Value is null || analysis.Value == 0m)
            return new(ExperimentProposalAction.Neutral, analysis.Reason);
        if (analysis.Value > 0m)
        {
            if (portfolio.PositionQuantity == 0m)
                return new(ExperimentProposalAction.Open, analysis.Reason);
            return worker.PriorFavorableMarkPrice is decimal favorableMark
                && favorableMark > worker.AverageEntryPrice
                ? new(ExperimentProposalAction.Add, analysis.Reason)
                : new(ExperimentProposalAction.Neutral, "Position addition requires a fresh favorable mark above average entry.");
        }
        return portfolio.PositionQuantity > 0m
            ? new(ExperimentProposalAction.Reduce, analysis.Reason)
            : new(ExperimentProposalAction.Neutral, "Bearish observation has no exposure to reduce.");
    }
}

public sealed class InMemoryExperimentDecisionLedger : IExperimentDecisionLedger
{
    private readonly ConcurrentDictionary<ExperimentDecisionKey, ExperimentDecisionRecord> _records = new();
    public Task<(ExperimentDecisionWriteResult Result, ExperimentDecisionRecord? Record)> RecordAsync(Guid userId, ExperimentDecisionRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        if (userId == Guid.Empty || record.Key.UserId != userId) throw new InvalidOperationException("Decision records must be written by their owner.");
        if (_records.TryAdd(record.Key, record)) return Task.FromResult((ExperimentDecisionWriteResult.Inserted, (ExperimentDecisionRecord?)record));
        var existing = _records[record.Key];
        return Task.FromResult((Equivalent(existing, record) ? ExperimentDecisionWriteResult.Duplicate : ExperimentDecisionWriteResult.Conflict, (ExperimentDecisionRecord?)existing));
    }
    public Task<IReadOnlyList<ExperimentDecisionRecord>> ListAsync(Guid userId, Guid workerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userId == Guid.Empty || workerId == Guid.Empty) throw new ArgumentException("Owner and worker are required.");
        return Task.FromResult<IReadOnlyList<ExperimentDecisionRecord>>(_records.Values.Where(x => x.Key.UserId == userId && x.Key.WorkerId == workerId).OrderBy(x => x.Key.AsOfUtc).ToArray());
    }
    internal static bool Equivalent(ExperimentDecisionRecord left, ExperimentDecisionRecord right) =>
        left.Proposal == right.Proposal && string.Equals(left.EvidenceFingerprint, right.EvidenceFingerprint, StringComparison.Ordinal);
}
