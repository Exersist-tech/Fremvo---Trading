namespace Trading.Application.Execution;

using Trading.Risk;

/// <summary>
/// The operator-controlled bounds on live trading.
/// </summary>
/// <remarks>
/// <para>
/// These are platform ceilings, not user preferences. A user can trade below
/// them and has no path to raise them. They are deliberately small by default:
/// a deployment that forgets to configure them still cannot send a large
/// order.
/// </para>
/// <para>
/// The proving instrument list is empty by default, which blocks every proving
/// order until an operator names the instruments. "Not configured" must read as
/// "nothing approved".
/// </para>
/// </remarks>
public sealed class LiveTradingOptions
{
    private decimal _maxOrderNotional = 100m;

    /// <summary>
    /// Required immutable platform ceilings for every live order. A missing
    /// hierarchy blocks an order rather than falling back to a user setting.
    /// </summary>
    public RiskLimitHierarchy? PlatformRiskLimits { get; init; } =
        new(platformMaxExposure: 100m, platformMaxPositionSize: 100m);

    /// <summary>
    /// The largest notional, in the instrument's quote currency, that any live
    /// order may carry regardless of stage.
    /// </summary>
    public decimal MaxOrderNotional
    {
        get => _maxOrderNotional;
        set => _maxOrderNotional = value > 0m
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The maximum order notional must be positive.");
    }

    /// <summary>
    /// The instruments an account in the proving stage may trade.
    /// </summary>
    public IReadOnlyCollection<string> ProvingSymbols { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The ceiling applied to an account the first time it is promoted out of
    /// paper, so a promoted account is never left without one.
    /// </summary>
    public decimal DefaultProvingNotionalCeiling { get; set; } = 25m;

    /// <summary>
    /// The initial live-trading cohort. An empty list permits nobody, which
    /// makes a route enabled without an explicitly named cohort harmless.
    /// </summary>
    public IReadOnlyCollection<Guid> AllowedUserIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// True only for a user the operator explicitly added to the initial
    /// rollout cohort.
    /// </summary>
    public bool CanTradeLive(Guid userId) =>
        userId != Guid.Empty && AllowedUserIds.Contains(userId);
}
