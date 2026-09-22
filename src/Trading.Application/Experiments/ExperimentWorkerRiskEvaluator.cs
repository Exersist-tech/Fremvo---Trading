using Trading.Domain.Experiments;
using Trading.Domain.Strategies;
using Trading.Domain.Universe;
using Trading.Risk;

namespace Trading.Application.Experiments;

/// <summary>
/// Explicit, owner-bound evidence that a research-group approval applies to a worker.
/// It is deliberately not derived from user or strategy parameters.
/// </summary>
public sealed record ExperimentWorkerRiskApprovalProvenance(
    Guid UserId,
    Guid WorkerId,
    ExperimentResearchGroup Group,
    int GroupConfigurationVersion,
    string StrategyId,
    bool IsApproved);

/// <summary>Worker-local ceilings. They can tighten platform limits, never widen them.</summary>
public sealed record ExperimentWorkerRiskBudget(
    decimal MaxOrderQuantity,
    decimal MaxExposure,
    int MaxAdditionsPerPosition);

/// <summary>Owner-scoped account and exposure evidence used for one fill evaluation.</summary>
public sealed record ExperimentWorkerExposureSnapshot(
    Guid UserId,
    Guid WorkerId,
    decimal PositionQuantity,
    decimal CurrentExposure,
    int AdditionCount);

/// <summary>
/// All evidence required to evaluate an experimental paper fill. Increasing positions require
/// every field below; missing evidence is a denial rather than an implicit default.
/// </summary>
public sealed record ExperimentWorkerRiskEvaluationRequest(
    ExperimentWorker Worker,
    ExperimentWorkerRiskApprovalProvenance Approval,
    ExperimentWorkerRiskBudget Budget,
    ExperimentWorkerExposureSnapshot Exposure,
    Guid InstrumentId,
    InstrumentEligibility Eligibility,
    EligibilityScope EligibilityScope,
    TimeSpan EligibilityEvidenceMaximumAge,
    StalenessPolicy StalenessPolicy,
    DateTimeOffset MarketDataAsOfUtc,
    DateTimeOffset AccountDataAsOfUtc,
    DateTimeOffset EvaluatedAtUtc,
    TradingModeFlags Mode,
    HaltSwitch? HaltSwitch,
    bool AccountHalted,
    bool StrategyHalted,
    bool MarketHalted,
    bool EmergencyStop,
    RiskLimitHierarchy PlatformLimits,
    ExperimentProposalAction ProposedAction,
    ExperimentPaperFillRequest ProposedFill);

public sealed record ExperimentWorkerRiskEvaluation(bool IsAllowed, string Reason, RiskEvaluationResult? PlatformEvaluation = null);

/// <summary>
/// Adapter between an isolated experiment fill and the platform risk primitives. It owns no
/// routing or account access and makes an approval, paper eligibility, current exposure, and
/// fresh market/account evidence mandatory before a position can increase.
/// </summary>
public sealed class ExperimentWorkerRiskEvaluator
{
    private readonly RiskEngine _riskEngine;

    public ExperimentWorkerRiskEvaluator(RiskEngine riskEngine)
    {
        _riskEngine = riskEngine ?? throw new ArgumentNullException(nameof(riskEngine));
    }

    public ExperimentWorkerRiskEvaluation Evaluate(ExperimentWorkerRiskEvaluationRequest? request)
    {
        if (request is null)
            return Denied("Worker risk evidence is required.");

        if (request.ProposedFill is null || request.Worker is null || request.Approval is null || request.Budget is null
            || request.Exposure is null || request.Eligibility is null || request.Mode is null
            || request.StalenessPolicy is null || request.PlatformLimits is null)
            return Denied("Worker risk policy is incomplete.");

        var worker = request.Worker;
        var fill = request.ProposedFill;
        var increasing = fill.Direction == Trading.Domain.Execution.TradeDirection.Buy;

        if (!increasing)
            return EvaluateSafetyExit(request);

        if (request.ProposedAction is not (ExperimentProposalAction.Open or ExperimentProposalAction.Add)
            || fill.Direction != Trading.Domain.Execution.TradeDirection.Buy)
            return Denied("Only an approved open or favorable-add proposal may increase an experiment position.");
        if (request.ProposedAction == ExperimentProposalAction.Open && worker.PositionQuantity != 0m)
            return Denied("An open proposal requires a flat experiment worker.");
        if (request.ProposedAction == ExperimentProposalAction.Add
            && (worker.PositionQuantity <= 0m
                || worker.PriorFavorableMarkPrice is not decimal favorableMark
                || favorableMark <= worker.AverageEntryPrice
                || fill.ReferencePrice < worker.AverageEntryPrice))
            return Denied("A position add requires existing exposure and a favorable mark above average entry; averaging down is forbidden.");
        if (request.Approval.UserId == Guid.Empty || request.Approval.WorkerId != worker.Id
            || request.Approval.UserId != worker.UserId || !request.Approval.IsApproved
            || request.Approval.GroupConfigurationVersion <= 0
            || !string.Equals(request.Approval.StrategyId, worker.StrategyId, StringComparison.Ordinal))
            return Denied("Worker group approval provenance is absent, foreign, revoked, or mismatched.");
        if (request.Exposure.UserId != worker.UserId || request.Exposure.WorkerId != worker.Id
            || request.Exposure.PositionQuantity != worker.PositionQuantity
            || request.Exposure.AdditionCount != worker.AdditionCount
            || request.Exposure.CurrentExposure < 0m)
            return Denied("Worker exposure evidence is absent, foreign, or inconsistent.");
        if (request.InstrumentId == Guid.Empty || request.Eligibility.InstrumentId != request.InstrumentId
            || request.EligibilityScope.Purpose != EligibilityPurpose.Paper
            || request.EligibilityScope.ProductType != TradingProductType.Spot
            || request.EligibilityEvidenceMaximumAge < TimeSpan.Zero
            || !request.Eligibility.IsGranted(request.EligibilityScope, request.EvaluatedAtUtc, request.EligibilityEvidenceMaximumAge))
            return Denied("Paper instrument eligibility is missing, stale, or does not match the requested spot scope.");
        if (request.Budget.MaxOrderQuantity <= 0m || request.Budget.MaxExposure <= 0m
            || request.Budget.MaxAdditionsPerPosition < 0)
            return Denied("Worker risk budget is invalid.");
        if (fill.RequestedQuantity > request.Budget.MaxOrderQuantity)
            return Denied("Requested quantity exceeds the worker-specific budget; quantity was not clamped.");
        if (worker.PositionQuantity > 0m
            && (worker.AdditionCount >= request.Budget.MaxAdditionsPerPosition
                || worker.AdditionCount >= worker.PositionControls.MaxAdditionsPerPosition))
            return Denied("Maximum worker position additions reached.");

        var proposedExposure = checked(fill.RequestedQuantity * fill.ReferencePrice);
        var nextQuantity = checked(worker.PositionQuantity + fill.RequestedQuantity);
        if (nextQuantity > worker.PositionControls.MaxPositionQuantity
            || proposedExposure + request.Exposure.CurrentExposure > worker.PositionControls.MaxPositionNotional
            || worker.TotalPurchasedQuantity + fill.RequestedQuantity > worker.PositionControls.MaxTotalPurchasedQuantity
            || worker.TotalPurchasedNotional + proposedExposure > worker.PositionControls.MaxTotalPurchasedNotional)
            return Denied("Requested fill exceeds immutable worker paper-position controls.");
        if (proposedExposure + request.Exposure.CurrentExposure > request.Budget.MaxExposure)
            return Denied("Requested exposure exceeds the worker-specific budget; exposure was not clamped.");

        var platform = _riskEngine.Evaluate(
            proposedExposure, request.Exposure.CurrentExposure, 0m, 0, worker.PositionQuantity > 0m ? 1 : 0,
            request.PlatformLimits.PlatformMaxPositionSize, request.PlatformLimits.PlatformMaxExposure,
            false, request.AccountHalted, request.StrategyHalted, false, false, false, false,
            request.MarketHalted, IsEmergencyStopped(request), request.Mode, request.HaltSwitch,
            request.StalenessPolicy, request.PlatformLimits, null, request.EvaluatedAtUtc, null,
            fill.RequestedQuantity, true, request.MarketDataAsOfUtc, request.AccountDataAsOfUtc);
        return platform.IsAllowed
            ? new(true, platform.Reason ?? "Worker fill is within risk limits.", platform)
            : new(false, platform.Reason ?? "Platform risk evaluation denied the worker fill.", platform);
    }

    private ExperimentWorkerRiskEvaluation EvaluateSafetyExit(ExperimentWorkerRiskEvaluationRequest request)
    {
        if (request.ProposedAction is not (ExperimentProposalAction.Reduce or ExperimentProposalAction.Close)
            || request.ProposedFill.Direction != Trading.Domain.Execution.TradeDirection.Sell)
            return Denied("Experiment action and fill direction do not describe a safety reduction.");
        if (request.ProposedFill.RequestedQuantity > request.Worker.PositionQuantity)
            return Denied("Safety reduction cannot sell more than the worker holds.");

        var platform = _riskEngine.Evaluate(
            0m, 0m, 0m, 0, request.Worker.PositionQuantity > 0m ? 1 : 0,
            request.PlatformLimits.PlatformMaxPositionSize, request.PlatformLimits.PlatformMaxExposure,
            false, request.AccountHalted, request.StrategyHalted, false, true, false, false,
            request.MarketHalted, IsEmergencyStopped(request), request.Mode, request.HaltSwitch,
            request.StalenessPolicy, request.PlatformLimits, null, request.EvaluatedAtUtc, null,
            request.ProposedFill.RequestedQuantity, false, request.MarketDataAsOfUtc, request.AccountDataAsOfUtc);
        return platform.IsAllowed
            ? new(true, platform.Reason ?? "Safety reduction is allowed.", platform)
            : new(false, platform.Reason ?? "Safety reduction is denied by platform policy.", platform);
    }

    private static ExperimentWorkerRiskEvaluation Denied(string reason) => new(false, reason);

    private static bool IsEmergencyStopped(ExperimentWorkerRiskEvaluationRequest request) =>
        request.EmergencyStop || (request.HaltSwitch?.Scope == HaltScope.Global && request.HaltSwitch.IsEnabled);
}
