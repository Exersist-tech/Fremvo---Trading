namespace Trading.Risk;

/// <summary>
/// Immutable ceilings for a trade. Limits are resolved in this order:
/// platform, account, user, then strategy. A missing child limit is not a
/// disabled platform limit; it simply contributes no further restriction.
/// </summary>
public sealed class RiskLimitHierarchy
{
    public RiskLimitHierarchy(
        decimal platformMaxExposure,
        decimal platformMaxPositionSize,
        decimal? accountMaxExposure = null,
        decimal? accountMaxPositionSize = null,
        decimal? userMaxExposure = null,
        decimal? userMaxPositionSize = null,
        decimal? strategyMaxExposure = null,
        decimal? strategyMaxPositionSize = null)
    {
        PlatformMaxExposure = RequirePositive(platformMaxExposure, nameof(platformMaxExposure));
        PlatformMaxPositionSize = RequirePositive(platformMaxPositionSize, nameof(platformMaxPositionSize));
        AccountMaxExposure = ValidateOptional(accountMaxExposure, nameof(accountMaxExposure));
        AccountMaxPositionSize = ValidateOptional(accountMaxPositionSize, nameof(accountMaxPositionSize));
        UserMaxExposure = ValidateOptional(userMaxExposure, nameof(userMaxExposure));
        UserMaxPositionSize = ValidateOptional(userMaxPositionSize, nameof(userMaxPositionSize));
        StrategyMaxExposure = ValidateOptional(strategyMaxExposure, nameof(strategyMaxExposure));
        StrategyMaxPositionSize = ValidateOptional(strategyMaxPositionSize, nameof(strategyMaxPositionSize));
    }

    public decimal PlatformMaxExposure { get; }

    /// <summary>Maximum order quantity. This is never compared with notional.</summary>
    public decimal PlatformMaxPositionSize { get; }

    public decimal? AccountMaxExposure { get; }

    public decimal? AccountMaxPositionSize { get; }

    public decimal? UserMaxExposure { get; }

    public decimal? UserMaxPositionSize { get; }

    public decimal? StrategyMaxExposure { get; }

    public decimal? StrategyMaxPositionSize { get; }

    public decimal EffectiveMaxExposure => MostRestrictive(
        PlatformMaxExposure, AccountMaxExposure, UserMaxExposure, StrategyMaxExposure);

    public decimal EffectiveMaxPositionSize => MostRestrictive(
        PlatformMaxPositionSize, AccountMaxPositionSize, UserMaxPositionSize, StrategyMaxPositionSize);

    public decimal GetEffectiveLimit(RiskLimitType type) =>
        type switch
        {
            RiskLimitType.MaxExposure => EffectiveMaxExposure,
            RiskLimitType.MaxPositionSize or RiskLimitType.MaxOrderSize => EffectiveMaxPositionSize,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "This hierarchy does not define that risk dimension.")
        };

    private static decimal RequirePositive(decimal value, string name)
    {
        if (value <= 0m)
        {
            throw new ArgumentOutOfRangeException(name, "A mandatory platform ceiling must be positive.");
        }

        return value;
    }

    private static decimal? ValidateOptional(decimal? value, string name)
    {
        if (value is <= 0m)
        {
            throw new ArgumentOutOfRangeException(name, "An optional limit must be positive when specified; use null when it is not set.");
        }

        return value;
    }

    private static decimal MostRestrictive(decimal platform, params decimal?[] children)
    {
        var result = platform;
        foreach (var child in children)
        {
            if (child.HasValue && child.Value < result)
            {
                result = child.Value;
            }
        }

        return result;
    }
}
