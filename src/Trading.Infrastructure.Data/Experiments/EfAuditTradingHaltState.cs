using Microsoft.EntityFrameworkCore;
using Trading.Application.Pipeline;
using Trading.Domain.Execution;
using Trading.Risk;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>Reads the latest audited platform, market, user, and strategy controls across hosts.</summary>
public sealed class EfAuditTradingHaltState(TradingDbContext db) : ITradingHaltState
{
    public async Task<TradingModeFlags> GetAsync(
        PipelineContext context, string symbol, Guid strategyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var userId = context.UserId.ToString("D");
        var strategy = strategyId.ToString("D");
        var market = symbol?.Trim() ?? string.Empty;
        var actions = await db.AuditEvents.AsNoTracking()
            .Where(value => value.TargetType == "TradingHalt"
                && (value.TargetId == "platform" || value.TargetId == market
                    || value.TargetId == userId || value.TargetId == strategy))
            .OrderByDescending(value => value.OccurredAtUtc).ThenByDescending(value => value.Id)
            .Select(value => new { value.Action, value.TargetId })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        bool Engaged(string target, string engage, string release) =>
            actions.FirstOrDefault(value => value.TargetId.Equals(target, StringComparison.OrdinalIgnoreCase)
                && (value.Action == engage || value.Action == release))?.Action == engage;

        return new TradingModeFlags
        {
            EmergencyStop = Engaged("platform", "Trading.EmergencyStopEngaged", "Trading.EmergencyStopReleased"),
            MarketHalt = Engaged(market, "Trading.MarketHalted", "Trading.MarketResumed"),
            AccountHalted = Engaged(userId, "Trading.UserHalted", "Trading.UserResumed"),
            StrategyHalted = Engaged(strategy, "Trading.StrategyHalted", "Trading.StrategyResumed"),
            CloseOnlyMode = Engaged(userId, "Trading.CloseOnlyEnabled", "Trading.CloseOnlyDisabled"),
            ReduceOnlyMode = Engaged(userId, "Trading.ReduceOnlyEnabled", "Trading.ReduceOnlyDisabled")
        };
    }
}
