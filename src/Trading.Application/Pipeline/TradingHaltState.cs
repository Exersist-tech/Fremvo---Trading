using System.Collections.Concurrent;
using Trading.Domain.Execution;
using Trading.Risk;

namespace Trading.Application.Pipeline;

/// <summary>
/// Supplies the halt and restricted-mode state that applies to one trade, combining every scope:
/// platform-wide emergency stop, per-market halt, per-user halt, per-account halt, and
/// per-strategy halt, plus close-only and reduce-only restrictions.
/// </summary>
public interface ITradingHaltState
{
    Task<TradingModeFlags> GetAsync(
        PipelineContext context,
        string symbol,
        Guid strategyId,
        CancellationToken cancellationToken);
}

/// <summary>
/// In-process halt state. Every switch fails closed: a halt blocks trading, and clearing a halt is
/// always an explicit action. The emergency stop is platform-wide and overrides everything else.
/// </summary>
public sealed class InMemoryTradingHaltState : ITradingHaltState
{
    private readonly ConcurrentDictionary<string, byte> _marketHalts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte> _userHalts = new();
    private readonly ConcurrentDictionary<Guid, byte> _strategyHalts = new();
    private readonly ConcurrentDictionary<Guid, byte> _closeOnlyUsers = new();
    private readonly ConcurrentDictionary<Guid, byte> _reduceOnlyUsers = new();

    private int _emergencyStop;

    /// <summary>Platform-wide emergency stop. Blocks every new order for every user.</summary>
    public bool EmergencyStop => Volatile.Read(ref _emergencyStop) == 1;

    public void EngageEmergencyStop() => Volatile.Write(ref _emergencyStop, 1);

    public void ReleaseEmergencyStop() => Volatile.Write(ref _emergencyStop, 0);

    public void HaltMarket(string symbol) => _marketHalts[Require(symbol)] = 1;

    public void ResumeMarket(string symbol) => _marketHalts.TryRemove(Require(symbol), out _);

    public void HaltUser(Guid userId) => _userHalts[userId] = 1;

    public void ResumeUser(Guid userId) => _userHalts.TryRemove(userId, out _);

    public void HaltStrategy(Guid strategyId) => _strategyHalts[strategyId] = 1;

    public void ResumeStrategy(Guid strategyId) => _strategyHalts.TryRemove(strategyId, out _);

    public void SetCloseOnly(Guid userId, bool enabled)
    {
        if (enabled)
        {
            _closeOnlyUsers[userId] = 1;
        }
        else
        {
            _closeOnlyUsers.TryRemove(userId, out _);
        }
    }

    public void SetReduceOnly(Guid userId, bool enabled)
    {
        if (enabled)
        {
            _reduceOnlyUsers[userId] = 1;
        }
        else
        {
            _reduceOnlyUsers.TryRemove(userId, out _);
        }
    }

    public Task<TradingModeFlags> GetAsync(
        PipelineContext context,
        string symbol,
        Guid strategyId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var flags = new TradingModeFlags
        {
            EmergencyStop = EmergencyStop,
            MarketHalt = !string.IsNullOrWhiteSpace(symbol) && _marketHalts.ContainsKey(symbol.Trim()),

            // A user halt and an account halt both stop that user's trading. Account-level halts
            // are surfaced through the same flag because a halted user's accounts are all halted.
            AccountHalted = _userHalts.ContainsKey(context.UserId),
            StrategyHalted = _strategyHalts.ContainsKey(strategyId),
            CloseOnlyMode = _closeOnlyUsers.ContainsKey(context.UserId),
            ReduceOnlyMode = _reduceOnlyUsers.ContainsKey(context.UserId)
        };

        return Task.FromResult(flags);
    }

    private static string Require(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(symbol));
        }

        return symbol.Trim();
    }
}
