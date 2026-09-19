namespace Trading.Risk;

public sealed class RiskLimitHierarchy
{
    public RiskLimitHierarchy(
        decimal platformMaxExposure,
        decimal platformMaxPositionSize,
        decimal? userMaxExposure = null,
        decimal? userMaxPositionSize = null)
    {
        if (platformMaxExposure < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(platformMaxExposure), "Platform maximum exposure cannot be negative.");
        }

        if (platformMaxPositionSize < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(platformMaxPositionSize), "Platform maximum position size cannot be negative.");
        }

        PlatformMaxExposure = platformMaxExposure;
        PlatformMaxPositionSize = platformMaxPositionSize;
        UserMaxExposure = userMaxExposure;
        UserMaxPositionSize = userMaxPositionSize;

        ValidateUserOverrides();
    }

    public decimal PlatformMaxExposure { get; }

    public decimal PlatformMaxPositionSize { get; }

    public decimal? UserMaxExposure { get; }

    public decimal? UserMaxPositionSize { get; }

    public decimal EffectiveMaxExposure => UserMaxExposure.HasValue
        ? Math.Min(UserMaxExposure.Value, PlatformMaxExposure)
        : PlatformMaxExposure;

    public decimal EffectiveMaxPositionSize => UserMaxPositionSize.HasValue
        ? Math.Min(UserMaxPositionSize.Value, PlatformMaxPositionSize)
        : PlatformMaxPositionSize;

    public void ValidateUserOverrides()
    {
        if (UserMaxExposure.HasValue && UserMaxExposure.Value > PlatformMaxExposure)
        {
            throw new InvalidOperationException("User-configured exposure exceeds the platform ceiling.");
        }

        if (UserMaxPositionSize.HasValue && UserMaxPositionSize.Value > PlatformMaxPositionSize)
        {
            throw new InvalidOperationException("User-configured position size exceeds the platform ceiling.");
        }
    }

    public decimal GetEffectiveLimit(RiskLimitType type)
    {
        return type switch
        {
            RiskLimitType.MaxExposure => EffectiveMaxExposure,
            RiskLimitType.MaxPositionSize => EffectiveMaxPositionSize,
            _ => 0m
        };
    }
}
